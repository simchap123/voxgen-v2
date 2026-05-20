namespace VoxGen.Desktop.Settings;

public enum HotkeyMode
{
    Hold,
    Toggle,
}

public enum AppTheme
{
    Light,
    Dark,
    System,
}

/// <summary>
/// Strongly-typed user settings — PRD §10 rule 2, §11 schema.
/// Immutable record: mutate only via <see cref="SettingsService.TryUpdate"/> using <c>with</c> expressions.
/// There are intentionally no API key fields in v1 (PRD §11, §18.1).
/// </summary>
public sealed record AppSettings
{
    /// <summary>Bumped when the schema changes; the store migrates forward (PRD §11).</summary>
    public int SettingsSchemaVersion { get; init; } = 1;

    /// <summary>Stable device ID (authoritative for selection).</summary>
    public string? SelectedMicrophoneId { get; init; }

    /// <summary>Display name only — never match on this; the ID is authoritative.</summary>
    public string? SelectedMicrophoneName { get; init; }

    public HotkeyMode HotkeyMode { get; init; } = HotkeyMode.Hold;

    /// <summary>Key combination string, e.g. <c>"RightAlt"</c> or <c>"Ctrl+Shift+Space"</c>.</summary>
    public string HotkeyValue { get; init; } = "RightAlt";

    public bool CleanupEnabled { get; init; } = true;

    public bool SaveTextHistoryLocal { get; init; } = true;

    /// <summary>PRD §8.11 — audio history defaults OFF.</summary>
    public bool SaveAudioHistoryLocal { get; init; } = false;

    public bool UseLocalHistoryForAi { get; init; } = false;

    public bool StartupOnBoot { get; init; } = false;

    public bool OverlayEnabled { get; init; } = true;

    /// <summary>
    /// Opt-in live/streaming dictation: types words as you speak instead of pasting on release.
    /// Default OFF — the reliable hold→release→paste path stays the default (PRD §20 streaming).
    /// </summary>
    public bool LiveTypingEnabled { get; init; } = false;

    /// <summary>Transcription language code (e.g. "en", "es"). null = auto-detect.</summary>
    public string? Language { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>Last app version that ran with these settings; drives schema migration.</summary>
    public string AppVersion { get; init; } = "2.0.0";

    /// <summary>Shipped defaults — used only on first run.</summary>
    public static AppSettings Defaults => new();
}
