using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Settings;
using VoxGen.Desktop.WindowDetection;

namespace VoxGen.Desktop.Hotkeys;

/// <summary>
/// Win32 implementation of <see cref="IHotkeyService"/>.
///
/// ARCHITECTURE NOTES — read before touching this file.
///
/// 1. Modifier+key hotkeys (e.g. Ctrl+Shift+Space) use the Win32 <c>RegisterHotKey</c> API.
///    That API requires an HWND to receive WM_HOTKEY. We create a dedicated, hidden
///    HWND_MESSAGE window on a background thread that runs a Win32 message pump for
///    the lifetime of the service. We do NOT piggy-back on the WPF dispatcher window
///    because (a) the tray app may not have a main window at all and (b) keeping the
///    hotkey thread independent of the UI thread means a hung UI can't block a hotkey
///    press. The dedicated thread also lets us own the WH_KEYBOARD_LL hook lifetime
///    in one place — Windows requires the thread that installed the hook to be the
///    one running the message loop that services it.
///
/// 2. Modifier-only hotkeys (e.g. RightAlt alone) CANNOT use RegisterHotKey — that
///    API requires a non-modifier main key. Instead, on the same message-pump thread
///    we install a low-level keyboard hook (<c>SetWindowsHookEx WH_KEYBOARD_LL</c>) that
///    watches for the bare modifier going down with no other modifiers held.
///
/// 3. RELEASE DETECTION FOR HOLD MODE — the non-obvious bit:
///    RegisterHotKey only signals presses. To detect the corresponding release we
///    need a low-level keyboard hook regardless. So:
///       Hold mode, modifier+key:  RegisterHotKey for press + WH_KEYBOARD_LL for release
///       Hold mode, modifier-only: WH_KEYBOARD_LL for both press AND release
///       Toggle mode, modifier+key:  RegisterHotKey only (no release needed for the key itself)
///       Toggle mode, modifier-only: WH_KEYBOARD_LL with a key-up edge to flip the toggle
///
/// 4. FOREGROUND CAPTURE — PRD §8.5. <see cref="ForegroundWindow.CaptureNow"/> runs
///    INSIDE the Win32 callbacks before any logging/event dispatch. That ordering is
///    load-bearing; do not refactor it to run after logging or on a different thread.
///
/// 5. THREADING — events (<see cref="Pressed"/>/<see cref="Released"/>) fire on the
///    hotkey thread. Subscribers that touch WPF UI must marshal back to the UI thread
///    themselves; doing it here would couple the service to WPF.
/// </summary>
public sealed class Win32HotkeyService : IHotkeyService
{
    // ---- Win32 constants ----
    private const int WM_HOTKEY        = 0x0312;
    private const int WM_USER_REGISTER = 0x0400 + 1;   // sent to the pump thread to (re)register
    private const int WM_USER_QUIT     = 0x0400 + 2;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    // Win32 MOD_* values for RegisterHotKey
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_WIN      = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes for left/right modifiers (we collapse them to "the modifier is down"
    // for press detection, but the LL hook gives us per-side info so we can match sided bindings).
    private const uint VK_LSHIFT   = 0xA0;
    private const uint VK_RSHIFT   = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU    = 0xA4; // left Alt
    private const uint VK_RMENU    = 0xA5; // right Alt
    private const uint VK_LWIN     = 0x5B;
    private const uint VK_RWIN     = 0x5C;
    private const uint VK_SHIFT    = 0x10;
    private const uint VK_CONTROL  = 0x11;
    private const uint VK_MENU     = 0x12; // Alt

    private const int HOTKEY_ID = 0xB0BA; // arbitrary; we only ever register one

    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    // Serializes (Un)RegisterAsync — the inter-thread baton is single-slot, so we never
    // want two callers racing on it. Doesn't gate the hotkey delivery path itself.
    private readonly SemaphoreSlim _registrationGate = new(1, 1);

    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private TaskCompletionSource<bool>? _pumpReady;

    // Owned by the pump thread once running:
    private IntPtr _messageWindow;
    private IntPtr _hookHandle;
    private GCHandle _hookCallbackHandle;     // keeps the delegate alive while the hook is installed
    private LowLevelKeyboardProc? _hookCallbackRef;

    // Current registration — guarded by _stateLock.
    private HotkeyDefinition? _hotkey;
    private HotkeyMode _mode;
    private bool _registeredWithRegisterHotKey;
    private bool _toggleOn;

    // Tracks which modifiers we believe to be down (from the hook), so modifier-only bindings
    // can verify nothing else is held. Mirrors the user's keyboard, not the OS at any instant —
    // good enough because the hook sees every key event.
    private HotkeyModifiers _liveModifiers;

    public event EventHandler<HotkeyPressedEventArgs>? Pressed;
    public event EventHandler? Released;

    public Win32HotkeyService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ---------- public surface ----------

    public async Task RegisterAsync(HotkeyDefinition hotkey, HotkeyMode mode, CancellationToken ct = default)
    {
        if (hotkey is null) throw new ArgumentNullException(nameof(hotkey));
        await DispatchToPumpAsync(new PendingRegistration(hotkey, mode), ct).ConfigureAwait(false);
    }

    public Task UnregisterAsync()
    {
        if (_pumpThreadId == 0) return Task.CompletedTask;
        return DispatchToPumpAsync(PendingRegistration.Empty, CancellationToken.None);
    }

    private async Task DispatchToPumpAsync(PendingRegistration request, CancellationToken ct)
    {
        await EnsurePumpRunningAsync().ConfigureAwait(false);
        await _registrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                _pendingRegistration = request;
                _registrationResult = tcs;
            }

            if (!PostThreadMessage(_pumpThreadId, WM_USER_REGISTER, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException("Failed to dispatch hotkey registration to the message pump thread.");
            }

            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            var error = await tcs.Task.ConfigureAwait(false);
            if (error is not null) throw error;
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_pumpThreadId != 0)
        {
            try { PostThreadMessage(_pumpThreadId, WM_USER_QUIT, IntPtr.Zero, IntPtr.Zero); }
            catch { /* nothing useful to do */ }
        }
        try { _pumpThread?.Join(TimeSpan.FromSeconds(2)); } catch { }

        if (_hookCallbackHandle.IsAllocated) _hookCallbackHandle.Free();
        _registrationGate.Dispose();
    }

    // ---------- pump thread ----------

    // Inter-thread baton for pending (un)registration. The pump thread reads this on WM_USER_REGISTER.
    private PendingRegistration? _pendingRegistration;
    private TaskCompletionSource<Exception?>? _registrationResult;

    private sealed record PendingRegistration(HotkeyDefinition? Hotkey, HotkeyMode Mode)
    {
        public static readonly PendingRegistration Empty = new((HotkeyDefinition?)null, HotkeyMode.Hold);
    }

    private Task EnsurePumpRunningAsync()
    {
        lock (_stateLock)
        {
            if (_pumpThread is not null && _pumpThread.IsAlive)
                return _pumpReady?.Task ?? Task.CompletedTask;

            _pumpReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pumpThread = new Thread(PumpThreadMain)
            {
                IsBackground = true,
                Name = "VoxGen.Hotkeys.MessagePump",
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();
            return _pumpReady.Task;
        }
    }

    private void PumpThreadMain()
    {
        _pumpThreadId = GetCurrentThreadId();

        try
        {
            _messageWindow = CreateMessageOnlyWindow();
            // Hold a strong ref to the callback delegate for the lifetime of the hook.
            _hookCallbackRef = HookCallback;
            _hookCallbackHandle = GCHandle.Alloc(_hookCallbackRef);
            _pumpReady?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _pumpReady?.TrySetException(ex);
            return;
        }

        // Standard Win32 message loop. GetMessage returns 0 on WM_QUIT, -1 on error.
        while (true)
        {
            int got = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (got == 0 || got == -1) break;

            if (msg.hwnd == IntPtr.Zero)
            {
                // Thread-targeted message (e.g. WM_USER_REGISTER, WM_USER_QUIT).
                if (msg.message == WM_USER_QUIT) break;
                if (msg.message == WM_USER_REGISTER) HandleRegistrationRequest();
                continue;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        TeardownPumpThreadState();
    }

    private void TeardownPumpThreadState()
    {
        try
        {
            if (_registeredWithRegisterHotKey)
            {
                UnregisterHotKey(_messageWindow, HOTKEY_ID);
                _registeredWithRegisterHotKey = false;
            }
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            if (_messageWindow != IntPtr.Zero)
            {
                DestroyWindow(_messageWindow);
                _messageWindow = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Hotkey teardown raised", new() { ["error"] = ex.Message });
        }
    }

    private void HandleRegistrationRequest()
    {
        PendingRegistration? pending;
        TaskCompletionSource<Exception?>? result;
        lock (_stateLock)
        {
            pending = _pendingRegistration;
            result = _registrationResult;
            _pendingRegistration = null;
            _registrationResult = null;
        }
        if (pending is null) return;

        try
        {
            ApplyRegistration(pending.Hotkey, pending.Mode);
            result?.TrySetResult(null);
        }
        catch (Exception ex)
        {
            result?.TrySetResult(ex);
        }
    }

    private void ApplyRegistration(HotkeyDefinition? hotkey, HotkeyMode mode)
    {
        // Tear down any prior registration first.
        if (_registeredWithRegisterHotKey)
        {
            UnregisterHotKey(_messageWindow, HOTKEY_ID);
            _registeredWithRegisterHotKey = false;
        }
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        lock (_stateLock)
        {
            _hotkey = hotkey;
            _mode = mode;
            _toggleOn = false;
            _liveModifiers = HotkeyModifiers.None;
        }

        if (hotkey is null) return; // pure unregister

        // We always install the low-level hook — it's needed for release detection in hold mode
        // and for modifier-only bindings in either mode. RegisterHotKey on top is an optimization
        // for modifier+key combos so the OS does the matching for us.
        InstallLowLevelHook();

        if (!hotkey.IsModifierOnly)
        {
            uint modFlags = ToWin32Modifiers(hotkey.Modifiers) | MOD_NOREPEAT;
            if (!RegisterHotKey(_messageWindow, HOTKEY_ID, modFlags, hotkey.VirtualKeyCode))
            {
                int err = Marshal.GetLastWin32Error();
                // Uninstall the hook so we don't leak it on a failed registration.
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
                _logger.Warning("RegisterHotKey failed", new() { ["hotkey"] = hotkey.ToString(), ["err"] = err });
                throw new HotkeyAlreadyInUseException(hotkey,
                    $"The hotkey '{hotkey}' is already in use by another application (Win32 error {err}).");
            }
            _registeredWithRegisterHotKey = true;
        }

        _logger.Info("Hotkey registered", new()
        {
            ["hotkey"] = hotkey.ToString(),
            ["mode"] = mode.ToString(),
            ["modifierOnly"] = hotkey.IsModifierOnly,
        });
    }

    private void InstallLowLevelHook()
    {
        // hMod must be the module handle of a DLL that contains the proc (or a process-wide module).
        // The .NET runtime's User32 calls accept the EXE's own module handle here; we use GetModuleHandle(null)
        // which is documented to return the calling process's image. The hook is GLOBAL because dwThreadId = 0.
        IntPtr hMod = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallbackRef!, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetWindowsHookEx failed (Win32 error {err}).");
        }
    }

    // ---------- callbacks ----------

    /// <summary>
    /// WindowProc for our message-only HWND. Handles WM_HOTKEY — fires on the configured
    /// modifier+key press. Release for hold mode comes from the LL hook, not from here.
    /// </summary>
    private IntPtr MessageWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            // PRD §8.5 — capture the foreground window FIRST, before any logging or event dispatch.
            var hwnd = ForegroundWindow.CaptureNow();
            var nowUtc = DateTime.UtcNow;
            HandlePress(hwnd, nowUtc);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Low-level keyboard hook. Sees every key on the system before the foreground app does.
    /// Responsibilities:
    ///  - Track live modifier state so modifier-only bindings can check "nothing else held".
    ///  - Fire press for modifier-only bindings on the right key-down.
    ///  - Fire release for hold mode when the bound key (or its modifier) comes up.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        try
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int eventType = wParam.ToInt32();
            bool isDown = eventType == WM_KEYDOWN || eventType == WM_SYSKEYDOWN;
            bool isUp   = eventType == WM_KEYUP   || eventType == WM_SYSKEYUP;

            // Track live modifier state regardless of whether we have a registered hotkey.
            UpdateLiveModifiers(info.vkCode, isDown, isUp);

            HotkeyDefinition? hk;
            HotkeyMode mode;
            lock (_stateLock) { hk = _hotkey; mode = _mode; }
            if (hk is null) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (hk.IsModifierOnly)
            {
                HandleModifierOnly(hk, mode, info.vkCode, isDown, isUp);
            }
            else
            {
                // For modifier+key hotkeys we rely on RegisterHotKey to detect presses (via WM_HOTKEY)
                // but we still need release-detection here for hold mode.
                if (mode == HotkeyMode.Hold && isUp && IsReleaseEventFor(hk, info.vkCode))
                {
                    HandleRelease();
                }
            }
        }
        catch (Exception ex)
        {
            // Hooks must NEVER throw — Windows will quietly remove the hook on the next call.
            _logger.Error("Hotkey hook callback raised", new() { ["error"] = ex.Message });
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void HandleModifierOnly(HotkeyDefinition hk, HotkeyMode mode, uint vk, bool isDown, bool isUp)
    {
        // Map the bound modifier to the set of VK codes that count as "the bound key".
        // For a sided binding we match exactly that side; for a non-sided (multi-mod or generic) we
        // accept either side.
        bool match = MatchesModifierOnlyKey(hk, vk);
        if (!match) return;

        // For modifier-only bindings, the binding must be the ONLY modifier held — otherwise
        // every Ctrl press while typing Ctrl+C would fire. We allow the bound modifier(s) themselves
        // in the live mask, but nothing else.
        bool noExtraneousModifiers = (_liveModifiers & ~hk.Modifiers) == HotkeyModifiers.None;
        if (!noExtraneousModifiers) return;

        if (isDown)
        {
            // Avoid auto-repeat: the LL hook delivers a stream of WM_KEYDOWNs while the key is held.
            // For hold mode we only want the first edge.
            if (mode == HotkeyMode.Hold && _modOnlyDown) return;
            _modOnlyDown = true;

            var hwnd = ForegroundWindow.CaptureNow();
            var nowUtc = DateTime.UtcNow;
            HandlePress(hwnd, nowUtc);
        }
        else if (isUp)
        {
            _modOnlyDown = false;
            if (mode == HotkeyMode.Hold) HandleRelease();
        }
    }

    private bool _modOnlyDown; // tracks the edge for modifier-only hold mode

    private bool MatchesModifierOnlyKey(HotkeyDefinition hk, uint vk)
    {
        // Sided display names. We disambiguate by DisplayName because the parser
        // resolves "RightAlt" => Modifiers=Alt, DisplayName="RightAlt".
        var name = hk.DisplayName;
        return name switch
        {
            "LeftAlt"    => vk == VK_LMENU,
            "RightAlt"   => vk == VK_RMENU,
            "LeftCtrl"   => vk == VK_LCONTROL,
            "RightCtrl"  => vk == VK_RCONTROL,
            "LeftShift"  => vk == VK_LSHIFT,
            "RightShift" => vk == VK_RSHIFT,
            "LeftWin"    => vk == VK_LWIN,
            "RightWin"   => vk == VK_RWIN,
            // Non-sided modifier-only (e.g. "Ctrl") — accept either side.
            _ => (hk.Modifiers, vk) switch
            {
                (HotkeyModifiers.Alt,     VK_LMENU)    => true,
                (HotkeyModifiers.Alt,     VK_RMENU)    => true,
                (HotkeyModifiers.Control, VK_LCONTROL) => true,
                (HotkeyModifiers.Control, VK_RCONTROL) => true,
                (HotkeyModifiers.Shift,   VK_LSHIFT)   => true,
                (HotkeyModifiers.Shift,   VK_RSHIFT)   => true,
                (HotkeyModifiers.Win,     VK_LWIN)     => true,
                (HotkeyModifiers.Win,     VK_RWIN)     => true,
                _ => false,
            },
        };
    }

    /// <summary>
    /// In hold mode we treat ANY of these as a release: the main key going up,
    /// or any required modifier going up. This means letting go of Shift in Ctrl+Shift+Space
    /// counts as a release — which is what users actually want; otherwise you can get
    /// stuck "recording" if you let go of Shift first.
    /// </summary>
    private static bool IsReleaseEventFor(HotkeyDefinition hk, uint vk)
    {
        if (vk == hk.VirtualKeyCode) return true;
        if ((hk.Modifiers & HotkeyModifiers.Alt)     != 0 && (vk == VK_LMENU    || vk == VK_RMENU    || vk == VK_MENU))    return true;
        if ((hk.Modifiers & HotkeyModifiers.Control) != 0 && (vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_CONTROL)) return true;
        if ((hk.Modifiers & HotkeyModifiers.Shift)   != 0 && (vk == VK_LSHIFT   || vk == VK_RSHIFT   || vk == VK_SHIFT))   return true;
        if ((hk.Modifiers & HotkeyModifiers.Win)     != 0 && (vk == VK_LWIN     || vk == VK_RWIN))                          return true;
        return false;
    }

    private void UpdateLiveModifiers(uint vk, bool isDown, bool isUp)
    {
        HotkeyModifiers bit = vk switch
        {
            VK_LMENU or VK_RMENU or VK_MENU       => HotkeyModifiers.Alt,
            VK_LCONTROL or VK_RCONTROL or VK_CONTROL => HotkeyModifiers.Control,
            VK_LSHIFT or VK_RSHIFT or VK_SHIFT    => HotkeyModifiers.Shift,
            VK_LWIN or VK_RWIN                    => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None,
        };
        if (bit == HotkeyModifiers.None) return;
        if (isDown) _liveModifiers |= bit;
        else if (isUp) _liveModifiers &= ~bit;
    }

    private void HandlePress(IntPtr hwnd, DateTime nowUtc)
    {
        HotkeyMode mode;
        lock (_stateLock) { mode = _mode; }

        if (mode == HotkeyMode.Hold)
        {
            FirePressed(hwnd, nowUtc);
            return;
        }

        // Toggle mode: flip the bit and emit the matching edge event.
        bool fireOn;
        lock (_stateLock)
        {
            _toggleOn = !_toggleOn;
            fireOn = _toggleOn;
        }
        if (fireOn) FirePressed(hwnd, nowUtc);
        else        FireReleased();
    }

    private void HandleRelease()
    {
        // For hold mode this is the natural release. For toggle mode we never reach here
        // (the modifier+key release in toggle mode is a no-op; the next press is what flips).
        HotkeyMode mode;
        lock (_stateLock) { mode = _mode; }
        if (mode == HotkeyMode.Hold) FireReleased();
    }

    private void FirePressed(IntPtr hwnd, DateTime nowUtc)
    {
        try { Pressed?.Invoke(this, new HotkeyPressedEventArgs(hwnd, nowUtc)); }
        catch (Exception ex) { _logger.Error("Hotkey Pressed handler raised", new() { ["error"] = ex.Message }); }
    }

    private void FireReleased()
    {
        try { Released?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.Error("Hotkey Released handler raised", new() { ["error"] = ex.Message }); }
    }

    private static uint ToWin32Modifiers(HotkeyModifiers m)
    {
        uint f = 0;
        if ((m & HotkeyModifiers.Alt)     != 0) f |= MOD_ALT;
        if ((m & HotkeyModifiers.Control) != 0) f |= MOD_CONTROL;
        if ((m & HotkeyModifiers.Shift)   != 0) f |= MOD_SHIFT;
        if ((m & HotkeyModifiers.Win)     != 0) f |= MOD_WIN;
        return f;
    }

    // ---------- message-only window plumbing ----------

    private WndProcDelegate? _wndProcRef;

    private IntPtr CreateMessageOnlyWindow()
    {
        const string className = "VoxGen.Hotkeys.MessageWindow";
        _wndProcRef = MessageWindowProc;

        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProcRef,
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        // RegisterClass returns 0 on duplicate class name (harmless if we registered before).
        _ = RegisterClass(ref wc);

        // HWND_MESSAGE = (HWND)(-3) creates a message-only window — no UI, just a message sink.
        IntPtr HWND_MESSAGE = new(-3);
        var hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowEx failed (Win32 error {err}).");
        }
        return hwnd;
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
