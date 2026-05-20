using System;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Settings;

namespace VoxGen.Desktop.Hotkeys;

/// <summary>
/// Global hotkey service — PRD §8.3. Supports hold-to-record and toggle modes,
/// emits a press event carrying the foreground HWND captured before any VoxGen
/// UI gets a chance to steal focus (PRD §8.5), and a release event.
///
/// Implementations must be disposable — the underlying Win32 resources
/// (hotkey registration, low-level hook, message-pump thread) need explicit teardown.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>
    /// Register the hotkey. Replaces any previous registration on the same service instance.
    /// </summary>
    /// <exception cref="HotkeyAlreadyInUseException">If <c>RegisterHotKey</c> reports the combination is taken.</exception>
    Task RegisterAsync(HotkeyDefinition hotkey, HotkeyMode mode, CancellationToken ct = default);

    /// <summary>Tear down the current registration. Safe to call when nothing is registered.</summary>
    Task UnregisterAsync();

    /// <summary>
    /// Fires once per "press" — for hold mode, on key-down; for toggle mode, on the transition to ON.
    /// Args carry the foreground HWND captured at the instant of press (PRD §8.5).
    /// </summary>
    event EventHandler<HotkeyPressedEventArgs> Pressed;

    /// <summary>
    /// Fires once per "release" — for hold mode, on key-up; for toggle mode, on the transition to OFF.
    /// </summary>
    event EventHandler Released;
}
