using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.License;

namespace VoxGen.Desktop.Backend;

/// <summary>
/// Options sent to the backend per transcription request (PRD §9).
/// </summary>
public sealed record TranscriptionOptions
{
    /// <summary>Transcription language code (e.g. "en"). <c>null</c> = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Per-request override of the global cleanup setting (PRD §8.7).</summary>
    public bool CleanupEnabled { get; init; } = true;

    public static readonly TranscriptionOptions Defaults = new();
}

/// <summary>
/// Raw transcription result as returned by the backend. Mapped onto
/// <see cref="Transcription.TranscriptionResult"/> by <see cref="Transcription.VoxGenManagedProvider"/>.
/// </summary>
public sealed record BackendTranscriptionResult
{
    [JsonPropertyName("final_text")] public required string FinalText { get; init; }
    [JsonPropertyName("raw_text")] public string? RawText { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("duration_ms")] public int DurationMs { get; init; }
    [JsonPropertyName("cleanup_applied")] public bool CleanupApplied { get; init; }
}

/// <summary>
/// HTTP client for the VoxGen-managed backend (PRD §9). HttpClient is injected so the
/// caller controls lifetime, timeouts, and base address; this class adds VoxGen-specific
/// headers and error mapping.
/// </summary>
public sealed class VoxGenBackendClient
{
    private const string UserAgent = "VoxGen/2.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// <paramref name="http"/> must already be configured with the backend base address
    /// (e.g. <c>new HttpClient { BaseAddress = new Uri(BackendConfig.VoxGenBackendBaseUrl) }</c>).
    /// </summary>
    public VoxGenBackendClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// POST /v1/transcribe (multipart). Sends the WAV bytes + options + bearer token,
    /// returns the final text. The audio is NOT persisted by the backend (PRD §9.3) and
    /// the WAV is NOT deleted by this method (PRD §5.3, §8.4 — caller owns audio lifecycle).
    /// </summary>
    public async Task<BackendTranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        string accessToken,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        if (wavBytes is null) throw new ArgumentNullException(nameof(wavBytes));
        if (string.IsNullOrEmpty(accessToken)) throw new ArgumentException("Access token required.", nameof(accessToken));
        if (options is null) throw new ArgumentNullException(nameof(options));

        using var content = new MultipartFormDataContent();

        var audio = new ByteArrayContent(wavBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, name: "audio", fileName: "recording.wav");

        if (!string.IsNullOrEmpty(options.Language))
        {
            content.Add(new StringContent(options.Language!), "language");
        }
        content.Add(new StringContent(options.CleanupEnabled ? "true" : "false"), "cleanup_enabled");

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/transcribe") { Content = content };
        ApplyDefaultHeaders(req, accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BackendUnavailableException("Could not reach VoxGen.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowFromErrorAsync(response, ct).ConfigureAwait(false);
            }

            var parsed = await response.Content.ReadFromJsonAsync<BackendTranscriptionResult>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (parsed is null)
            {
                throw new BackendUnavailableException("VoxGen returned an empty response.");
            }
            return parsed;
        }
    }

    /// <summary>
    /// GET /v1/license. Returns the user's current license/trial status. Errors map onto
    /// the same typed exceptions as <see cref="TranscribeAsync"/>.
    /// </summary>
    public async Task<LicenseStatus> ValidateLicenseAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) throw new ArgumentException("Access token required.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/license");
        ApplyDefaultHeaders(req, accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BackendUnavailableException("Could not reach VoxGen for license validation.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowFromErrorAsync(response, ct).ConfigureAwait(false);
            }

            var parsed = await response.Content.ReadFromJsonAsync<LicenseStatus>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (parsed is null)
            {
                throw new BackendUnavailableException("VoxGen returned an empty license response.");
            }
            return parsed;
        }
    }

    private static void ApplyDefaultHeaders(HttpRequestMessage req, string accessToken)
    {
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    /// <summary>
    /// Map a non-2xx response onto the right typed exception. Reads the body once as JSON
    /// to pull a server-supplied message if available; otherwise falls back to the reason phrase.
    /// </summary>
    private static async Task ThrowFromErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var message = await ExtractMessageAsync(response, ct).ConfigureAwait(false)
                      ?? response.ReasonPhrase
                      ?? "Backend request failed.";

        switch ((int)response.StatusCode)
        {
            case 401:
                throw new UnauthenticatedException(message);
            case 402:
                throw new TrialExpiredException(message);
            case 403:
                throw new QuotaExceededException(message);
            case 429:
                throw new RateLimitedException(message, ParseRetryAfter(response));
            case >= 500 and <= 599:
                throw new BackendUnavailableException(message);
            default:
                throw new VoxGenBackendException(
                    $"VoxGen returned {(int)response.StatusCode} {response.ReasonPhrase}: {message}");
        }
    }

    private static async Task<string?> ExtractMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var err = await response.Content.ReadFromJsonAsync<BackendErrorBody>(JsonOptions, ct).ConfigureAwait(false);
            return err?.Message ?? err?.Error;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;

        if (retryAfter.Delta is { } delta) return delta;
        if (retryAfter.Date is { } when_)
        {
            var diff = when_ - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }

        // Some servers send a bare number that .NET stuffs into neither Delta nor Date.
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
        }
        return null;
    }

    private sealed record BackendErrorBody
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }
}
