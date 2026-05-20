namespace VoxGen.Desktop.Settings;

/// <summary>
/// Durable settings backing store. PRD §10 rule 1 — there is exactly one of these per app instance.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Loads settings from durable storage. Returns <see cref="AppSettings.Defaults"/> when the file
    /// is missing (first run). <b>Throws</b> if the file exists but is unreadable / unparseable —
    /// PRD §10 forbids silently falling back to defaults, which would erase the user's choices.
    /// </summary>
    AppSettings Load();

    /// <summary>
    /// Atomically persists <paramref name="settings"/> and verifies the write by reading it back
    /// (PRD §10 rule 5, steps 2–3). Throws on any failure; <see cref="SettingsService"/> rolls back
    /// in-memory state on throw (rule 6).
    /// </summary>
    void SaveAndVerify(AppSettings settings);
}
