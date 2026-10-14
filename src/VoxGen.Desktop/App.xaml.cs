using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Clipboard;
using VoxGen.Desktop.Core;
using VoxGen.Desktop.History;
using VoxGen.Desktop.Hotkeys;
using VoxGen.Desktop.Installer;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Overlay;
using VoxGen.Desktop.ReadAloud;
using VoxGen.Desktop.Settings;
using VoxGen.Desktop.Transcription;
using VoxGen.Desktop.Tray;
using VoxGen.Desktop.UI;
using VoxGen.Desktop.Updater;

namespace VoxGen.Desktop;

public partial class App : Application
{
    private const string SingletonMutexName = @"Global\VoxGen-Desktop-Singleton-{D8F2E5F4-8A4F-4B1B-A7D6-9B5E2C3D1F4A}";

    /// <summary>Single source for the display version (logs + uninstall registry entry).</summary>
    private const string AppVersion = "2.4.0";

    private Mutex? _singletonMutex;
    private bool _ownsMutex;

    // Composition root. Public statics are intentionally kept thin Ã¢â‚¬â€ services
    // accept their dependencies via constructors; these exist only so the tray
    // event handlers and a few other places can reach the singletons.
    public static ILogger Logger { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;

    // Composed in OnStartup, disposed in OnExit.
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private Win32HotkeyService? _hotkeys;
    private HttpClient? _groqHttp;
    private HttpClient? _updateHttp;
    private ApiKeyStore? _apiKeys;
    private HistoryStore? _history;
    private UpdateService? _updates;
    private GroqWhisperProvider? _transcriber;
    private HoverReadService? _hoverReader;
    private SpeechPlayer? _speechPlayer;
    private ReadAloudBar? _readAloudBar;
    private DateTime _lastHoverErrorUtc = DateTime.MinValue;
    private bool _openedTtsTermsPage;
    private bool _isPaused;

    // Dictation pipeline (this slice).
    private IAudioDeviceEnumerator? _deviceEnumerator;
    private IAudioCapture? _audioCapture;
    private IClipboardPaste? _clipboardPaste;
    private OverlayWindow? _overlay;
    private WpfDispatcher? _dispatcher;
    private DictationController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Uninstall entry point: Windows "Installed apps Ã¢â€ â€™ Uninstall" runs `VoxGen.exe --uninstall`.
        // Tear down (registry entry + %APPDATA%\VoxGen + self-delete) and exit before any normal
        // init, so we don't take the singleton mutex or spin up the tray.
        if (Array.Exists(e.Args, a => string.Equals(a, InstallRegistration.UninstallArg, StringComparison.OrdinalIgnoreCase)))
        {
            InstallRegistration.Uninstall();
            Shutdown(exitCode: 0);
            return;
        }

        // After a self-update the previous process may still be exiting; wait for it to let go
        // of the singleton instead of silently quitting.
        var afterUpdate = Array.Exists(e.Args, a => string.Equals(a, UpdateService.AfterUpdateArg, StringComparison.OrdinalIgnoreCase));
        _singletonMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out _ownsMutex);
        for (var i = 0; afterUpdate && !_ownsMutex && i < 40; i++)
        {
            try { _ownsMutex = _singletonMutex.WaitOne(500); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }
        if (!_ownsMutex)
        {
            Shutdown(exitCode: 0);
            return;
        }

        Paths.EnsureCreated();
        Logger = new FileLogger(Paths.LogsDirectory);
        Logger.Info("VoxGen starting", new() { ["version"] = AppVersion, ["afterUpdate"] = afterUpdate });
        UpdateService.CleanupOldVersion();

        // Register a per-user "Installed apps" entry so the portable exe is uninstallable the normal
        // Windows way. Best-effort; no-ops for dev/`dotnet run`.
        InstallRegistration.EnsureRegistered(AppVersion, Logger);

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

        WireLocalServices();
        WireTray();
        WireDictation();
        WireHotkeys();
        WireSettingsReactions();
        WireHoverRead();
        PromptForApiKeyIfMissing();
        _ = Task.Run(BackgroundUpdateCheckAsync);

        if (afterUpdate)
        {
            _tray?.ShowBalloon("VoxGen", $"Updated to version {AppVersion}.");
        }

        base.OnStartup(e);
    }

    // -------- tray + settings window --------

    private void WireTray()
    {
        _tray = new TrayIcon();
        _tray.ShowSettingsRequested += (_, _) =>
        {
            ShowSettings(null);
        };
        _tray.ShowHistoryRequested += (_, _) => ShowSettings("History");
        _tray.PauseToggleRequested += (_, _) =>
        {
            _isPaused = _tray!.IsPaused;
            Logger.Info("Pause toggled", new() { ["paused"] = _isPaused });
            // DictationController reads () => _isPaused and ignores hotkey presses while paused.
        };
        _tray.QuitRequested += (_, _) => Shutdown();
    }

    private void ShowSettings(string? section)
    {
        _settingsWindow ??= new SettingsWindow(
            SettingsService, Logger, _deviceEnumerator!, _apiKeys!, _history!, _updates!, _groqHttp!,
            shutdownForUpdate: () => Dispatcher.BeginInvoke(() => Shutdown()),
            previewSpeech: text => _speechPlayer?.Speak(text));
        if (section is not null) _settingsWindow.ShowSectionByName(section);
        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    // -------- local services: API key, Groq, history, updates --------

    private void WireLocalServices()
    {
        _apiKeys = new ApiKeyStore(Paths.ApiKeyFile, Logger);
        _history = new HistoryStore(Paths.HistoryFile, Paths.RecordingsDirectory, Logger);
        _groqHttp = new HttpClient { BaseAddress = new Uri(GroqWhisperProvider.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _transcriber = new GroqWhisperProvider(
            _groqHttp,
            () => _apiKeys.Get(),
            () => SettingsService.Current.CleanupEnabled,
            () => SettingsService.Current.Language,
            Logger);

        _updateHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _updates = new UpdateService(_updateHttp, Version.Parse(AppVersion), Logger);
        Logger.Info("Local services ready", new() { ["hasApiKey"] = _apiKeys.HasKey, ["canSelfUpdate"] = _updates.CanSelfUpdate });
    }

    /// <summary>Hold Ctrl+Shift + hover Ã¢â€ â€™ Groq reads the text under the mouse.</summary>
    private void WireHoverRead()
    {
        var speech = new GroqSpeech(_groqHttp!, () => _apiKeys!.Get());
        _speechPlayer = new SpeechPlayer(
            speech,
            voice: () => SettingsService.Current.HoverReadVoice,
            initialRate: SettingsService.Current.HoverReadRate,
            reportError: message =>
            {
                // Groq makes each account accept the Orpheus voice terms once. Open that page for the
                // user (once per run) instead of showing a raw API error.
                if (message.Contains("terms acceptance", StringComparison.OrdinalIgnoreCase))
                {
                    if (_openedTtsTermsPage) return;
                    _openedTtsTermsPage = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "https://console.groq.com/playground?model=canopylabs%2Forpheus-v1-english",
                                UseShellExecute = true,
                            });
                        }
                        catch (Exception ex) { Logger.Error("Failed to open Groq terms page", new() { ["error"] = ex.Message }); }
                        _tray?.ShowBalloon("VoxGen read-aloud",
                            "Groq needs you to accept the voice terms once. Accept them on the page that just opened, then try again.");
                    });
                    return;
                }
                // At most one balloon per 30 s so a bad key or offline spell doesn't spam.
                if (DateTime.UtcNow - _lastHoverErrorUtc < TimeSpan.FromSeconds(30)) return;
                _lastHoverErrorUtc = DateTime.UtcNow;
                Dispatcher.BeginInvoke(() => _tray?.ShowBalloon("VoxGen read-aloud", message));
            },
            Logger);

        _readAloudBar = new ReadAloudBar(_speechPlayer, persistRate: rate =>
        {
            if (!SettingsService.TryUpdate(c => c with { HoverReadRate = rate }, out var error))
                Logger.Warning("Failed to save read-aloud speed", new() { ["error"] = error });
        });

        _hoverReader = new HoverReadService(
            _speechPlayer,
            enabled: () => SettingsService.Current.HoverReadEnabled && !_isPaused && _apiKeys!.HasKey,
            shortcut: () => SettingsService.Current.HoverReadShortcut,
            Logger);
        _hoverReader.Start();
    }

    /// <summary>First run (or key removed): open Settings on the API key page.</summary>
    private void PromptForApiKeyIfMissing()
    {
        if (_apiKeys!.HasKey) return;
        try
        {
            ShowSettings("ApiKey");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to open settings for API key", new() { ["error"] = ex.Message });
        }
    }

    private async Task BackgroundUpdateCheckAsync()
    {
        if (_updates is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var update = await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            if (update is null) return;
            Logger.Info("Update available", new() { ["tag"] = update.Tag });
            await Dispatcher.InvokeAsync(() =>
            {
                _settingsWindow?.ShowUpdate(update);
                _tray?.ShowBalloon("VoxGen", $"Version {update.Version.ToString(3)} is ready. Open Settings Ã¢â€ â€™ About and click Update now.");
            });
        }
        catch (Exception ex)
        {
            Logger.Warning("Background update check failed", new() { ["error"] = ex.Message });
        }
    }

    /// <summary>Saves a finished dictation to local history per the user's settings.</summary>
    private void SaveToHistory(AudioClip clip, TranscriptionResult result)
    {
        var s = SettingsService.Current;
        if (!s.SaveTextHistoryLocal && !s.SaveAudioHistoryLocal) return;
        _history?.Add(
            result.FinalText,
            result.RawText,
            clip.Duration,
            s.SaveAudioHistoryLocal ? clip.WavBytes : null,
            s.SaveTextHistoryLocal);
    }

    // -------- dictation pipeline --------

    private void WireDictation()
    {
        _deviceEnumerator = new NAudioDeviceEnumerator(Logger);
        _audioCapture = new NAudioCapture(Logger);
        _clipboardPaste = new ClipboardPaste(Logger);
        _overlay = new OverlayWindow();
        _dispatcher = new WpfDispatcher(Dispatcher);

        // Groq direct with the user's own key. With no key saved, the provider throws
        // MissingApiKeyException and the overlay says "Add your Groq API key in Settings".
        ITranscriptionProvider provider = _transcriber!;

        // Pre-warm the selected mic so the first hotkey press starts capturing immediately (Ã‚Â§8.4).
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
            keystrokeTyper: new KeystrokeTyper(Logger),
            onTranscribed: SaveToHistory);
    }

    // -------- hotkeys --------

    private void WireHotkeys()
    {
        _hotkeys = new Win32HotkeyService(Logger);

        // The controller is the sole subscriber to the hotkey; Attach marshals pump-thread
        // events onto the UI dispatcher and runs the recordÃ¢â€ â€™transcribeÃ¢â€ â€™paste state machine.
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
        // window itself stays decoupled from capture/hotkey services (PRD Ã‚Â§10 Ã¢â‚¬â€ one source of truth).
        SettingsService.Changed += (_, e) =>
        {
            if (e.Previous.SelectedMicrophoneId != e.Current.SelectedMicrophoneId
                && _audioCapture is { IsRecording: false })
            {
                try { _audioCapture.Initialize(e.Current.SelectedMicrophoneId); }
                catch (Exception ex) { Logger.Error("Microphone re-init failed", new() { ["error"] = ex.Message }); }
            }

            if (Math.Abs(e.Previous.HoverReadRate - e.Current.HoverReadRate) > 0.001)
            {
                _speechPlayer?.SetRate(e.Current.HoverReadRate);
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
        try { _hoverReader?.Dispose(); } catch { /* best effort */ }
        try { _speechPlayer?.Dispose(); } catch { /* best effort */ }
        try { _readAloudBar?.Close(); } catch { /* best effort */ }
        try { _audioCapture?.Dispose(); } catch { /* best effort */ }
        try { _overlay?.Close(); } catch { /* best effort */ }
        _tray?.Dispose();
        _groqHttp?.Dispose();
        _updateHttp?.Dispose();

        if (_ownsMutex)
        {
            try { _singletonMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned Ã¢â‚¬â€ ignore */ }
        }
        _singletonMutex?.Dispose();
        base.OnExit(e);
    }
}
