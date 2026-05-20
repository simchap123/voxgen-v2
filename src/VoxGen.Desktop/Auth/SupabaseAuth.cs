using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.Auth;

/// <summary>
/// REST client for Supabase GoTrue auth endpoints. PRD §6.2 — no Supabase SDK; HttpClient only.
///
/// Base URL and anon key are passed in via the constructor (not read from
/// <see cref="Backend.BackendConfig"/> directly) so tests can point this at a fake server.
/// </summary>
public sealed class SupabaseAuth
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _anonKey;

    public SupabaseAuth(HttpClient http, string baseUrl, string anonKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _anonKey = anonKey ?? throw new ArgumentNullException(nameof(anonKey));
    }

    /// <summary>POST /auth/v1/token?grant_type=password.</summary>
    public Task<SupabaseSession> SignInAsync(string email, string password, CancellationToken ct) =>
        PasswordGrantAsync(
            url: $"{_baseUrl}/auth/v1/token?grant_type=password",
            body: new { email, password },
            ct: ct);

    /// <summary>POST /auth/v1/signup. Returns a session immediately when email-confirmations are disabled,
    /// which is the assumption for v1 (PRD §8.2 — "straight to use" after sign-up).</summary>
    public Task<SupabaseSession> SignUpAsync(string email, string password, CancellationToken ct) =>
        PasswordGrantAsync(
            url: $"{_baseUrl}/auth/v1/signup",
            body: new { email, password },
            ct: ct);

    /// <summary>POST /auth/v1/token?grant_type=refresh_token.</summary>
    public Task<SupabaseSession> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PasswordGrantAsync(
            url: $"{_baseUrl}/auth/v1/token?grant_type=refresh_token",
            body: new { refresh_token = refreshToken },
            ct: ct);

    /// <summary>POST /auth/v1/logout. Best-effort: a network failure here should not block local sign-out.</summary>
    public async Task SignOutAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/auth/v1/logout");
        ApplyDefaultHeaders(req);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SupabaseAuthException(HttpStatusCode.ServiceUnavailable,
                "Could not reach Supabase to sign out.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowFromErrorAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<SupabaseSession> PasswordGrantAsync(string url, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        ApplyDefaultHeaders(req);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SupabaseAuthException(HttpStatusCode.ServiceUnavailable,
                "Could not reach Supabase.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowFromErrorAsync(response, ct).ConfigureAwait(false);
            }

            var raw = await response.Content.ReadFromJsonAsync<RawSupabaseTokenResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (raw is null || string.IsNullOrEmpty(raw.AccessToken) || string.IsNullOrEmpty(raw.RefreshToken))
            {
                throw new SupabaseAuthException(response.StatusCode,
                    "Supabase returned a 2xx response without a usable session.");
            }

            return new SupabaseSession
            {
                AccessToken = raw.AccessToken!,
                RefreshToken = raw.RefreshToken!,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(raw.ExpiresIn > 0 ? raw.ExpiresIn : 3600),
                Email = raw.User?.Email,
            };
        }
    }

    private void ApplyDefaultHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("apikey", _anonKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    private static async Task ThrowFromErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string message = response.ReasonPhrase ?? "Supabase request failed.";
        string? errorCode = null;
        try
        {
            var err = await response.Content.ReadFromJsonAsync<RawSupabaseError>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (err is not null)
            {
                // Supabase uses several shapes — surface whichever has content.
                message = err.ErrorDescription ?? err.Msg ?? err.Message ?? err.Error ?? message;
                errorCode = err.ErrorCode ?? err.Code;
            }
        }
        catch
        {
            // Body wasn't JSON — the reason phrase is the best we have.
        }
        throw new SupabaseAuthException(response.StatusCode, message, errorCode);
    }

    // --- internal wire formats ----------------------------------------------------

    private sealed record RawSupabaseTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("user")] public RawSupabaseUser? User { get; init; }
    }

    private sealed record RawSupabaseUser
    {
        [JsonPropertyName("email")] public string? Email { get; init; }
    }

    private sealed record RawSupabaseError
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; init; }
        [JsonPropertyName("msg")] public string? Msg { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("code")] public string? Code { get; init; }
    }
}
