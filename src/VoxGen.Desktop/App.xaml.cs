using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Auth;
using VoxGen.Desktop.Backend;
using VoxGen.Desktop.Clipboard;
using VoxGen.Desktop.Core;
using VoxGen.Desktop.Hotkeys;
using VoxGen.Desktop.License;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Overlay;
using VoxGen.Desktop.Settings;
using VoxGen.Desktop.Transcription;
using VoxGen.Desktop.Tray;
using VoxGen.Desktop.UI;

namespace VoxGen.Desktop;

public partial class App : Application
{
    private const string SingletonMutexName = @"Global\VoxGen-Desktop-Singleton-{D8F2E5F4-8A4F-4B1B-A7D6-9B5E2C3D1F4A}";

    private Mutex? _singletonMutex;
    private bool _ownsMutex;

    // Composition root. Public statics are intentionally kept thin — services
    // accept their dependencies via constructors; these exist only so the tray
    // event handlers and a few other places can reach the singletons.
    public static ILogger Logger { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;

    // Composed in OnStartup, disposed in OnExit.
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private Win32HotkeyService? _hotkeys;
    private HttpClient? _backendHttp;
    private HttpClient? _supabaseHttp;
    private SessionTokenStore? _sessionStore;
    private LicenseCheckCache? _licenseCache;
    private VoxGenBackendClient? _backendClient;
    private VoxGenManagedProvider? _transcriber;
    private SupabaseAuth? _supabaseAuth;
    private SessionManager? _sessionManager;
    private bool _isPaused;

    // Dictation pipeline (this slice).
    private IAudioDeviceEnumerator? _deviceEnumerator;
    private IAudioCapture? _audioCapture;
    private IClipboardPaste? _clipboardPaste;
    private OverlayWindow? _overlay;
    private WpfDispatcher? _dispatcher;
    private DictationController? _controller;
    private WhisperLocalTranscriptionProvider? _localWhisper;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown(exitCode: 0);
            return;
        }

        Paths.EnsureCreated();
        Logger = new FileLogger(Paths.LogsDirectory);
        Logger.Info("VoxGen starting", new() { ["version"] = "2.0.6" });

        try
        {
            var store = new JsonSettingsStore(Paths.SettingsFile, Logger);
            SettingsService = SettingsService.Load(store, Logger);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings on startup", new() { ["error"] = ex.Message });
            MessageBox.Show(
                "VoxGen could not load its settings file. Your previous settings have been preserved on disk; please contact support.",
                "VoxGen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
            return;
        }

        WireTray();
        WireAuth();
        WireBackend();
        WireDictation();
        WireHotkeys();
        WireSettingsReactions();
        MaybePromptSignIn();

        base.OnStartup(e);
    }

    // -------- tray + settings window --------

    private void WireTray()
    {
        _tray = new TrayIcon();
        _tray.ShowSettingsRequested += (_, _) =>
        {
            _settingsWindow ??= new SettingsWindow(SettingsService, Logger, _deviceEnumerator!, _sessionManager);
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }
            _settingsWindow.Activate();
        };
        _tray.PauseToggleRequested += (_, _) =>
        {
            _isPaused = _tray!.IsPaused;
            Logger.Info("Pause toggled", new() { ["paused"] = _isPaused });
            // DictationController reads () => _isPaused and ignores hotkey presses while paused.
        };
        _tray.QuitRequested += (_, _) => Shutdown();
    }

    // -------- auth (Supabase) --------

    private void WireAuth()
    {
        // Supabase is configured (URL + publishable key), so build the auth + session manager even
        // before the transcription backend exists — sign-in is needed the moment the backend goes live.
        if (!SupabaseConfigured())
        {
            Logger.Warning("Supabase not configured — sign-in unavailable");
            return;
        }
        try
        {
            _supabaseHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _supabaseAuth = new SupabaseAuth(_supabaseHttp, BackendConfig.SupabaseUrl, BackendConfig.SupabaseAnonKey);
            _sessionStore = new SessionTokenStore(Paths.SessionTokenFile, Logger);
            _sessionManager = new SessionManager(_supabaseAuth, _sessionStore, Logger);
            Logger.Info("Auth ready", new() { ["signedIn"] = _sessionManager.IsSignedIn });
        }
        catch (Exception ex)
        {
            Logger.Error("Auth init failed", new() { ["error"] = ex.Message });
        }
    }

    // -------- backend / transcription --------

    private void WireBackend()
    {
        // BackendConfig ships with REPLACE_AT_BUILD placeholders. Until the VoxGen backend URL is set
        // (after deploy), skip managed init — the app keeps using the local Whisper preview provider.
        if (!BackendConfigured())
        {
            Logger.Warning("Backend not configured — skipping backend init", new()
            {
                ["voxgenBaseUrl"] = BackendConfig.VoxGenBackendBaseUrl,
            });
            return;
        }

        if (_sessionManager is null)
        {
            Logger.Error("Backend configured but auth is not — cannot wire managed transcription");
            return;
        }

        try
        {
            _backendHttp = new HttpClient { BaseAddress = new Uri(BackendConfig.VoxGenBackendBaseUrl) };
            _licenseCache = new LicenseCheckCache(Paths.LicenseCacheFile, Logger);
            _backendClient = new VoxGenBackendClient(_backendHttp);

            // Real token source: the session manager refreshes proactively + on demand.
            var sessions = _sessionManager;
            Func<CancellationToken, Task<string>> getToken = ct => sessions.GetAccessTokenAsync(ct);

            _transcriber = new VoxGenManagedProvider(
                _backendClient,
                getToken,
                Logger,
                new TranscriptionOptions
                {
                    CleanupEnabled = SettingsService.Current.CleanupEnabled,
                    Language = SettingsService.Current.Language,
                });

            // Background license validation — keeps the offline cache fresh.
            if (_sessionManager.IsSignedIn)
            {
                _ = Task.Run(BackgroundValidateLicenseAsync);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Backend init failed", new() { ["error"] = ex.Message });
        }
    }

    private static bool SupabaseConfigured() =>
        !string.IsNullOrWhiteSpace(BackendConfig.SupabaseUrl)
        && BackendConfig.SupabaseUrl != "REPLACE_AT_BUILD"
        && !string.IsNullOrWhiteSpace(BackendConfig.SupabaseAnonKey)
        && BackendConfig.SupabaseAnonKey != "REPLACE_AT_BUILD";

    private static bool BackendConfigured() =>
        !string.IsNullOrWhiteSpace(BackendConfig.VoxGenBackendBaseUrl)
        && BackendConfig.VoxGenBackendBaseUrl != "REPLACE_AT_BUILD"
        && SupabaseConfigured();

    /// <summary>When the managed backend is live but no one's signed in, prompt sign-in (PRD §8.2).</summary>
    private void MaybePromptSignIn()
    {
        if (!BackendConfigured() || _sessionManager is null || _sessionManager.IsSignedIn) return;
        try
        {
            var window = new SignInWindow(_sessionManager, Logger);
            window.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show sign-in window", new() { ["error"] = ex.Message });
        }
    }

    private async Task BackgroundValidateLicenseAsync()
    {
        if (_backendClient is null || _licenseCache is null || _sessionManager is null) return;
        try
        {
            var token = await _sessionManager.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false);
            var status = await _backendClient.ValidateLicenseAsync(token, CancellationToken.None)
                                             .ConfigureAwait(false);
            _licenseCache.Save(status);
            Logger.Info("License validated", new() { ["state"] = status.State.ToString() });
        }
        catch (Exception ex)
        {
            if (_licenseCache.IsWithinOfflineGrace(LicenseCheckCache.DefaultGraceWindow))
            {
                Logger.Warning("License check failed — within offline grace", new() { ["error"] = ex.Message });
            }
            else
            {
                Logger.Error("License check failed — outside offline grace", new() { ["error"] = ex.Message });
                // TODO (overlay slice): notify the user via overlay/tray balloon.
            }
        }
    }

    // -------- dictation pipeline --------

    private void WireDictation()
    {
        _deviceEnumerator = new NAudioDeviceEnumerator(Logger);
        _audioCapture = new NAudioCapture(Logger);
        _clipboardPaste = new ClipboardPaste(Logger);
        _overlay = new OverlayWindow();
        _dispatcher = new WpfDispatcher(Dispatcher);

        // Provider selection. Managed backend when configured (it isn't yet — REPLACE_AT_BUILD).
        // Until then, DEV STOPGAP: local Whisper tiny.en transcribes real speech with no cloud key
        // (PRD §3.2 dev scaffold; remove with the Whisper.net packages before shipping). Falls back
        // to the canned stub if Whisper can't initialize (e.g. no internet on first-run download).
        ITranscriptionProvider provider;
        if (_transcriber is not null)
        {
            provider = _transcriber;
        }
        else
        {
            try
            {
                var whisper = new WhisperLocalTranscriptionProvider(Paths.ModelsDirectory, Logger);
                _localWhisper = whisper;
                provider = whisper;
                // Download + load the model in the background so the first hotkey press is fast.
                _ = Task.Run(() => whisper.WarmUpAsync());
                Logger.Info("Using WhisperLocalTranscriptionProvider (tiny.en) — dev stopgap, no backend");
            }
            catch (Exception ex)
            {
                Logger.Error("Whisper init failed — falling back to stub", new() { ["error"] = ex.Message });
                provider = new StubTranscriptionProvider();
            }
        }

        // Pre-warm the selected mic so the first hotkey press starts capturing immediately (§8.4).
        try
        {
            _audioCapture.Initialize(SettingsService.Current.SelectedMicrophoneId);
        }
        catch (Exception ex)
        {
            Logger.Error("Microphone initialization failed", new() { ["error"] = ex.Message });
        }

        _controller = new DictationController(
            _audioCapture, provider, _clipboardPaste, _overlay, () => _isPaused, Logger,
            liveTypingEnabled: () => SettingsService.Current.LiveTypingEnabled,
            keystrokeTyper: new KeystrokeTyper(Logger));
    }

    // -------- hotkeys --------

    private void WireHotkeys()
    {
        _hotkeys = new Win32HotkeyService(Logger);

        // The controller is the sole subscriber to the hotkey; Attach marshals pump-thread
        // events onto the UI dispatcher and runs the record→transcribe→paste state machine.
        _controller!.Attach(_hotkeys, _dispatcher!);

        // Fire-and-forget: registration touches Win32 internals on a dedicated thread
        // and can take ~tens of ms; we don't want to block startup. We log + surface
        // a tray balloon on the well-known "already in use" failure.
        _ = Task.Run(async () =>
        {
            try
            {
                var def = HotkeyDefinition.Parse(SettingsService.Current.HotkeyValue);
                await _hotkeys.RegisterAsync(def, SettingsService.Current.HotkeyMode).ConfigureAwait(false);
                Logger.Info("Hotkey registered", new()
                {
                    ["hotkey"] = def.ToString(),
                    ["mode"] = SettingsService.Current.HotkeyMode.ToString(),
                });
            }
            catch (HotkeyAlreadyInUseException ex)
            {
                Logger.Warning("Hotkey already in use", new() { ["hotkey"] = ex.Hotkey });
                Dispatcher.Invoke(() => _tray?.ShowBalloon(
                    "VoxGen",
                    $"The {ex.Hotkey} hotkey is already in use by another app. Pick a different one in Settings."));
            }
            catch (FormatException ex)
            {
                Logger.Error("Hotkey configured but unparseable", new()
                {
                    ["value"] = SettingsService.Current.HotkeyValue,
                    ["error"] = ex.Message,
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Hotkey registration failed", new() { ["error"] = ex.Message });
            }
        });
    }

    // -------- settings reactions --------

    private void WireSettingsReactions()
    {
        // Settings changes (from the Settings window) flow back into the runtime here, so the
        // window itself stays decoupled from capture/hotkey services (PRD §10 — one source of truth).
        SettingsService.Changed += (_, e) =>
        {
            if (e.Previous.SelectedMicrophoneId != e.Current.SelectedMicrophoneId
                && _audioCapture is { IsRecording: false })
            {
                try { _audioCapture.Initialize(e.Current.SelectedMicrophoneId); }
                catch (Exception ex) { Logger.Error("Microphone re-init failed", new() { ["error"] = ex.Message }); }
            }

            if (e.Previous.HotkeyValue != e.Current.HotkeyValue || e.Previous.HotkeyMode != e.Current.HotkeyMode)
            {
                _ = ReregisterHotkeyAsync();
            }
        };
    }

    private async Task ReregisterHotkeyAsync()
    {
        if (_hotkeys is null) return;
        try
        {
            await _hotkeys.UnregisterAsync().ConfigureAwait(false);
            var def = HotkeyDefinition.Parse(SettingsService.Current.HotkeyValue);
            await _hotkeys.RegisterAsync(def, SettingsService.Current.HotkeyMode).ConfigureAwait(false);
            Logger.Info("Hotkey re-registered", new() { ["hotkey"] = def.ToString() });
        }
        catch (HotkeyAlreadyInUseException ex)
        {
            Logger.Warning("Hotkey already in use", new() { ["hotkey"] = ex.Hotkey });
            Dispatcher.Invoke(() => _tray?.ShowBalloon(
                "VoxGen",
                $"The {ex.Hotkey} hotkey is already in use by another app. Pick a different one in Settings."));
        }
        catch (Exception ex)
        {
            Logger.Error("Hotkey re-registration failed", new() { ["error"] = ex.Message });
        }
    }

    // -------- shutdown --------

    protected override void OnExit(ExitEventArgs e)
    {
        Logger?.Info("VoxGen exiting", new() { ["exitCode"] = e.ApplicationExitCode });

        try { _settingsWindow?.ForceClose(); } catch { /* best effort */ }
        try { _hotkeys?.UnregisterAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        _hotkeys = null;
        try { _audioCapture?.Dispose(); } catch { /* best effort */ }
        try { _localWhisper?.Dispose(); } catch { /* best effort */ }
        try { _overlay?.Close(); } catch { /* best effort */ }
        _tray?.Dispose();
        _backendHttp?.Dispose();
        _supabaseHttp?.Dispose();

        if (_ownsMutex)
        {
            try { _singletonMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned — ignore */ }
        }
        _singletonMutex?.Dispose();
        base.OnExit(e);
    }
}
