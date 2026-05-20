using System;

namespace VoxGen.Desktop.Hotkeys;

/// <summary>
/// Carries the foreground window handle captured at the instant the hotkey was pressed —
/// PRD §8.5: the HWND must be captured before any VoxGen UI takes focus. Capture happens
/// inside the WM_HOTKEY / low-level-hook callback BEFORE this event is dispatched and
/// BEFORE any logging on the press path, otherwise the handle can drift to whatever
/// pops up next (overlay, notification, etc.).
/// </summary>
public sealed class HotkeyPressedEventArgs : EventArgs
{
    /// <summary>
    /// The HWND of the user's active window at the instant of the keypress.
    /// Used downstream to restore focus and target the paste. May be <see cref="IntPtr.Zero"/>
    /// if GetForegroundWindow returned null (rare: lock screen, UAC prompt, etc.).
    /// </summary>
    public IntPtr ForegroundWindowAtPress { get; }

    /// <summary>UTC timestamp of the press, captured at the same instant — for latency diagnostics.</summary>
    public DateTime PressedAtUtc { get; }

    public HotkeyPressedEventArgs(IntPtr foregroundWindowAtPress, DateTime pressedAtUtc)
    {
        ForegroundWindowAtPress = foregroundWindowAtPress;
        PressedAtUtc = pressedAtUtc;
    }
}
