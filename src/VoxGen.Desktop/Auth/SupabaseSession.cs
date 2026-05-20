using System;
using System.Text.Json.Serialization;

namespace VoxGen.Desktop.Auth;

/// <summary>
/// A live Supabase session. Returned by sign-in / sign-up / refresh. Persisted at rest
/// via <see cref="SessionTokenStore"/> (DPAPI-protected — PRD §6.2, §14.2).
///
/// JSON property names match Supabase's GoTrue REST shape (snake_case) so
/// <see cref="SupabaseAuth"/> can deserialize responses directly into this record.
/// </summary>
public sealed record SupabaseSession
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    /// <summary>
    /// Absolute UTC expiry. Computed from Supabase's <c>expires_in</c> (seconds) at
    /// the moment the response was received; not transmitted by Supabase directly.
    /// </summary>
    [JsonPropertyName("expires_at_utc")]
    public required DateTime ExpiresAtUtc { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }
}
