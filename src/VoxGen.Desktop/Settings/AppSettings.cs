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
/// The Groq API key is NOT here — it lives DPAPI-encrypted in <see cref="ApiKeyStore"/>.
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

    /// <summary>Keep each recording as a WAV in the local recordings folder (local only).</summary>
    public bool SaveAudioHistoryLocal { get; init; } = true;

    public bool UseLocalHistoryForAi { get; init; } = false;

    public bool StartupOnBoot { get; init; } = false;

    public bool OverlayEnabled { get; init; } = true;

    /// <summary>
    /// Opt-in live/streaming dictation: types words as you speak instead of pasting on release.
    /// Default OFF — the reliable hold→release→paste path stays the default (PRD §20 streaming).
    /// </summary>
    public bool LiveTypingEnabled { get; init; } = false;

    /// <summary>Hold Ctrl+Shift and rest the mouse on something to hear it read aloud (Groq voice).</summary>
    public bool HoverReadEnabled { get; init; } = true;

    /// <summary>Groq Orpheus voice id for read-aloud (see <c>GroqSpeech.Voices</c>).</summary>
    public string HoverReadVoice { get; init; } = "hannah";

    /// <summary>Keys held (with the mouse resting) to read aloud: "CtrlShift" or "CtrlAlt".</summary>
    public string HoverReadShortcut { get; init; } = "CtrlShift";

    /// <summary>Read-aloud playback speed, 0.5–2.5 (set from the control bar's slower/faster).</summary>
    public double HoverReadRate { get; init; } = 1.0;

    /// <summary>Transcription language code (e.g. "en", "es"). null = auto-detect.</summary>
    public string? Language { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>Last app version that ran with these settings; drives schema migration.</summary>
    public string AppVersion { get; init; } = "2.0.0";

    /// <summary>Shipped defaults — used only on first run.</summary>
    public static AppSettings Defaults => new();
}
