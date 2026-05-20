using System;
using System.Text.Json.Serialization;

namespace VoxGen.Desktop.License;

public enum LicenseState
{
    /// <summary>No subscription / account not yet activated.</summary>
    NotActivated,

    /// <summary>30-day metered trial (PRD §16).</summary>
    Trial,

    /// <summary>Paid subscription in good standing.</summary>
    Active,

    /// <summary>Trial ran out OR subscription lapsed. Local history is preserved; transcription is blocked (PRD §16.2).</summary>
    Expired,
}

/// <summary>
/// Snapshot of the user's license/subscription state from the backend. Cached locally
/// by <see cref="LicenseCheckCache"/> so the app keeps working through brief outages
/// (PRD §8.12 — offline grace window for license *checks*, not transcription).
/// </summary>
public sealed record LicenseStatus
{
    [JsonPropertyName("state")]
    public required LicenseState State { get; init; }

    /// <summary>Only meaningful when <see cref="State"/> is <see cref="LicenseState.Trial"/>.</summary>
    [JsonPropertyName("trial_days_left")]
    public int TrialDaysLeft { get; init; }

    /// <summary>Human-readable plan name, e.g. "Trial", "Pro Monthly". For display.</summary>
    [JsonPropertyName("plan_name")]
    public string? PlanName { get; init; }

    /// <summary>UTC instant at which this status was confirmed by the backend.</summary>
    [JsonPropertyName("validated_at_utc")]
    public required DateTime ValidatedAtUtc { get; init; }

    /// <summary>UTC instant the client should attempt the next validation.</summary>
    [JsonPropertyName("next_validation_at_utc")]
    public required DateTime NextValidationAtUtc { get; init; }
}
