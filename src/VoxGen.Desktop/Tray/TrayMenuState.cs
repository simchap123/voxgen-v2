namespace VoxGen.Desktop.Tray;

/// <summary>
/// Pure logic for the tray context menu — extracted from <see cref="TrayIcon"/>
/// so it can be unit-tested without a real <c>NotifyIcon</c> (PRD §10 rule 11 spirit:
/// any non-trivial UI logic gets a test).
/// </summary>
public sealed class TrayMenuState
{
    /// <summary>True when dictation is paused — drives the menu label and check mark.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Toggles paused state; returns the new value.</summary>
    public bool TogglePaused()
    {
        IsPaused = !IsPaused;
        return IsPaused;
    }

    /// <summary>The label the "Pause/Resume" menu item should show given current state.</summary>
    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>The tooltip the tray icon should show given current state.</summary>
    public string Tooltip => IsPaused ? "VoxGen — paused" : "VoxGen";
}
