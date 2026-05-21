using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Backend;
using VoxGen.Desktop.Clipboard;
using VoxGen.Desktop.Hotkeys;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Overlay;
using VoxGen.Desktop.Transcription;

namespace VoxGen.Desktop.Core;

public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
    Pasting,
}

/// <summary>
/// The recording state machine (PRD §7 core workflow). Sole subscriber to the hotkey service;
/// drives capture → transcription → output and updates the overlay at each transition.
///
/// Two output modes:
///  • <b>Commit-on-release</b> (default): record while held, transcribe once on release, paste.
///  • <b>Live typing</b> (opt-in, <see cref="DictationController"/> constructed with a keystroke
///    typer + enabled predicate): while held, transcribe the audio-so-far every ~1.2 s and type
///    the <i>stabilized prefix</i> (words two consecutive passes agree on) into the focused window,
///    append-only — no backspacing. On release, a final pass types any remaining words.
///
/// Threading: <see cref="HandlePressed"/>/<see cref="HandleReleasedAsync"/> run on the UI thread
/// (overlay is WPF, clipboard needs STA). The live loop runs on the thread pool; release cancels and
/// awaits it before the final pass, so the word-commit state is never touched concurrently.
///
/// Invariants: never lose the recording. On transcription failure the WAV is preserved (§8.4, §13);
/// on paste failure the text is left on the clipboard by the paste pipeline (§8.8).
/// </summary>
public sealed class DictationController
{
    // How often live mode re-transcribes the audio-so-far and commits newly-stable words.
    // Shorter = words appear more often (feels more live) at the cost of more CPU; tiny.en on
    // short clips is fast enough that ~600 ms self-throttles cleanly on longer utterances.
    private const int LiveIntervalMs = 600;

    private readonly IAudioCapture _capture;
    private readonly ITranscriptionProvider _provider;
    private readonly IClipboardPaste _paste;
    private readonly IRecordingOverlay _overlay;
    private readonly Func<bool> _isPaused;
    private readonly ILogger _logger;
    private readonly Func<AudioClip, string?> _preserveRecording;
    private readonly Func<bool> _liveTypingEnabled;
    private readonly IKeystrokeTyper? _typer;

    private IntPtr _targetWindow;

    // Live-typing state (only touched by the live loop, or by release after the loop is awaited).
    private CancellationTokenSource? _liveCts;
    private Task? _liveLoop;
    private readonly List<string> _committedWords = new();
    private string[]? _lastInterimWords;
    private bool _anyTyped;
    private bool _liveActive;

    public DictationState State { get; private set; } = DictationState.Idle;

    public DictationController(
        IAudioCapture capture,
        ITranscriptionProvider provider,
        IClipboardPaste paste,
        IRecordingOverlay overlay,
        Func<bool> isPaused,
        ILogger logger,
        Func<AudioClip, string?>? preserveRecording = null,
        Func<bool>? liveTypingEnabled = null,
        IKeystrokeTyper? keystrokeTyper = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _paste = paste ?? throw new ArgumentNullException(nameof(paste));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _isPaused = isPaused ?? throw new ArgumentNullException(nameof(isPaused));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _preserveRecording = preserveRecording ?? DefaultPreserveRecording;
        _liveTypingEnabled = liveTypingEnabled ?? (() => false);
        _typer = keystrokeTyper;
    }

    /// <summary>Subscribe to the hotkey service, marshalling its (pump-thread) events to the UI thread.</summary>
    public void Attach(IHotkeyService hotkeys, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(dispatcher);
        hotkeys.Pressed += (_, e) => dispatcher.Post(() => HandlePressed(e.ForegroundWindowAtPress));
        hotkeys.Released += (_, _) => dispatcher.Post(() => _ = HandleReleasedAsync());
    }

    /// <summary>Begin a recording. UI thread. Ignored when paused or not idle.</summary>
    public void HandlePressed(IntPtr foregroundWindow)
    {
        if (_isPaused())
        {
            _logger.Debug("Hotkey press ignored — paused");
            return;
        }
        if (State != DictationState.Idle)
        {
            _logger.Debug("Hotkey press ignored — not idle", new() { ["state"] = State.ToString() });
            return;
        }

        _targetWindow = foregroundWindow;
        try
        {
            _capture.Start();
            State = DictationState.Recording;
            _overlay.SetState(OverlayState.Recording);
            StartLiveIfEnabled();
            _logger.Info("Recording started", new() { ["hwnd"] = foregroundWindow.ToInt64(), ["live"] = _liveActive });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start recording", new() { ["error"] = ex.Message });
            _overlay.ShowError("Can't access the microphone");
            State = DictationState.Idle;
        }
    }

    /// <summary>Stop recording, transcribe, and output (paste, or type the remainder in live mode). UI thread.</summary>
    public async Task HandleReleasedAsync()
    {
        if (State != DictationState.Recording)
        {
            _logger.Debug("Hotkey release ignored — not recording", new() { ["state"] = State.ToString() });
            return;
        }

        State = DictationState.Transcribing;

        // Stop the live streaming loop (if any) before the final pass so word-commit state is stable.
        if (_liveActive)
        {
            _liveCts?.Cancel();
            try { if (_liveLoop is not null) await _liveLoop.ConfigureAwait(true); }
            catch (Exception ex) { _logger.Warning("Live loop end raised", new() { ["error"] = ex.Message }); }
            _liveCts?.Dispose();
            _liveCts = null;
            _liveLoop = null;
        }

        AudioClip? clip;
        try
        {
            clip = await _capture.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to stop capture", new() { ["error"] = ex.Message });
            _overlay.ShowError("Recording failed");
            _liveActive = false;
            State = DictationState.Idle;
            return;
        }

        if (clip is null || clip.WavBytes.Length == 0)
        {
            _logger.Warning("No audio captured");
            _overlay.SetState(OverlayState.Hidden);
            _liveActive = false;
            State = DictationState.Idle;
            return;
        }

        _overlay.SetState(OverlayState.Transcribing);

        TranscriptionResult result;
        try
        {
            result = await _provider.TranscribeAsync(clip, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // §8.4 / §13 / §5.3 — never lose the recording. Preserve the WAV so the user can retry.
            var savedPath = _preserveRecording(clip);
            _logger.Error("Transcription failed — recording preserved", new()
            {
                ["error"] = ex.Message,
                ["savedTo"] = savedPath ?? "(save failed)",
            });
            // Surface WHY it failed so the user can act (sign in / upgrade / wait), instead of a
            // single opaque message for every cause.
            _overlay.ShowError(TranscriptionErrorMessage(ex));
            _liveActive = false;
            State = DictationState.Idle;
            return;
        }

        if (_liveActive)
        {
            // Already typing live — type only the words not yet committed; never paste (would duplicate).
            CommitRemaining(result.FinalText);
            _overlay.SetState(OverlayState.Hidden);
            _logger.Info("Live dictation finalized", new() { ["words"] = _committedWords.Count });
            _liveActive = false;
        }
        else
        {
            State = DictationState.Pasting;
            var paste = _paste.PasteInto(_targetWindow, result.FinalText);
            if (paste.Outcome == PasteOutcome.LeftOnClipboard)
            {
                _logger.Warning("Paste failed — text left on clipboard", new() { ["error"] = paste.Error });
                _overlay.ShowError("Pasted to clipboard — press Ctrl+V");
            }
            else
            {
                _logger.Info("Dictation pasted", new() { ["chars"] = result.FinalText.Length });
                _overlay.SetState(OverlayState.Hidden);
            }
        }

        State = DictationState.Idle;
    }

    // ---------- live streaming ----------

    private void StartLiveIfEnabled()
    {
        _liveActive = _liveTypingEnabled() && _typer is not null;
        if (!_liveActive) return;

        _committedWords.Clear();
        _lastInterimWords = null;
        _anyTyped = false;
        _liveCts = new CancellationTokenSource();
        var token = _liveCts.Token;
        _liveLoop = Task.Run(() => LiveLoopAsync(token));
    }

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LiveIntervalMs, ct).ConfigureAwait(false);

                var wav = _capture.SnapshotWav();
                if (wav is null || wav.Length == 0) continue;

                TranscriptionResult interim;
                try
                {
                    interim = await _provider.TranscribeAsync(
                        new AudioClip { WavBytes = wav, SampleRate = 16000, Channels = 1, Duration = TimeSpan.Zero },
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Warning("Live interim transcription failed", new() { ["error"] = ex.Message });
                    continue;
                }

                CommitStable(interim.FinalText);
            }
        }
        catch (OperationCanceledException) { /* expected when release cancels the loop */ }
    }

    /// <summary>Type the leading words that two consecutive interim passes agree on (append-only).</summary>
    private void CommitStable(string text)
    {
        var words = Tokenize(text);
        int stable = _lastInterimWords is null ? 0 : CommonPrefixWords(_lastInterimWords, words);
        for (int i = _committedWords.Count; i < stable; i++)
        {
            TypeWord(words[i]);
            _committedWords.Add(words[i]);
        }
        _lastInterimWords = words;
    }

    /// <summary>On release, the final transcription is treated as fully stable: type whatever's left.</summary>
    private void CommitRemaining(string text)
    {
        var words = Tokenize(text);
        for (int i = _committedWords.Count; i < words.Length; i++)
        {
            TypeWord(words[i]);
            _committedWords.Add(words[i]);
        }
    }

    private void TypeWord(string word)
    {
        _typer!.TypeText(_anyTyped ? " " + word : word);
        _anyTyped = true;
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int CommonPrefixWords(string[] a, string[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && string.Equals(a[i], b[i], StringComparison.Ordinal)) i++;
        return i;
    }

    private string? DefaultPreserveRecording(AudioClip clip)
    {
        try
        {
            Directory.CreateDirectory(Paths.TempAudioDirectory);
            var path = Path.Combine(Paths.TempAudioDirectory, $"recording-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
            File.WriteAllBytes(path, clip.WavBytes);
            return path;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to preserve recording", new() { ["error"] = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// Map a transcription failure to a short, actionable overlay message. The recording is always
    /// preserved regardless (§5.3); this just tells the user what to do next.
    /// </summary>
    private static string TranscriptionErrorMessage(Exception ex) => ex switch
    {
        TrialExpiredException => "Trial ended — upgrade in Settings",
        QuotaExceededException => "Usage limit reached — try later",
        RateLimitedException => "Too many requests — wait a moment",
        UnauthenticatedException => "Sign in to dictate (Settings)",
        BackendUnavailableException => "Can't reach VoxGen — recording saved",
        InvalidOperationException when ex.Message.Contains("signed in", StringComparison.OrdinalIgnoreCase)
            => "Sign in to dictate (Settings)",
        _ => "Couldn't transcribe — recording saved",
    };
}
