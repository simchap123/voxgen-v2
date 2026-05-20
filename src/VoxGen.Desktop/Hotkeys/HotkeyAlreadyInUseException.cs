using System;

namespace VoxGen.Desktop.Hotkeys;

/// <summary>
/// Thrown when a hotkey can't be registered because another process owns it
/// (Win32 <c>RegisterHotKey</c> returned false). PRD §8.3 calls for graceful
/// handling — the App layer catches this and surfaces it to the user, then
/// either falls back to a different hotkey or guides the user into settings.
/// </summary>
public sealed class HotkeyAlreadyInUseException : Exception
{
    public HotkeyDefinition Hotkey { get; }

    public HotkeyAlreadyInUseException(HotkeyDefinition hotkey)
        : base($"The hotkey '{hotkey}' is already in use by another application.")
    {
        Hotkey = hotkey;
    }

    public HotkeyAlreadyInUseException(HotkeyDefinition hotkey, string message)
        : base(message)
    {
        Hotkey = hotkey;
    }
}
