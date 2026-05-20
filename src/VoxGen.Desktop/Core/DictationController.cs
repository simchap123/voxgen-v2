using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Audio;
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
/// drives capture → transcription → paste and updates the overlay at each transition.
///
/// Threading: <see cref="HandlePressed"/> and <see cref="HandleReleasedAsync"/> must run on the UI
/// thread (the overlay is WPF, the clipboard requires STA). <see cref="Attach"/> wires the hotkey
/// events — which fire on the hotkey pump thread — and marshals them onto the UI thread via the
/// injected <see cref="IUiDispatcher"/>. The methods are public so tests can drive them directly
/// with fakes, no dispatcher needed.
///
/// Invariants: never lose the recording. On transcription failure the WAV is preserved to disk
/// (PRD §8.4, §13); on paste failure the text is left on the clipboard by the paste pipeline (§8.8).
/// </summary>
public sealed class DictationController
{
    private readonly IAudioCapture _capture;
    private readonly ITranscriptionProvider _provider;
    private readonly IClipboardPaste _paste;
    private readonly IRecordingOverlay _overlay;
    private readonly Func<bool> _isPaused;
    private readonly ILogger _logger;
    private readonly Func<AudioClip, string?> _preserveRecording;

    private IntPtr _targetWindow;

    public DictationState State { get; private set; } = DictationState.Idle;

    public DictationController(
        IAudioCapture capture,
        ITranscriptionProvider provider,
        IClipboardPaste paste,
        IRecordingOverlay overlay,
        Func<bool> isPaused,
        ILogger logger,
        Func<AudioClip, string?>? preserveRecording = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _paste = paste ?? throw new ArgumentNullException(nameof(paste));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _isPaused = isPaused ?? throw new ArgumentNullException(nameof(isPaused));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _preserveRecording = preserveRecording ?? DefaultPreserveRecording;
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

        // §8.5 — the foreground window was captured by the hotkey service at the instant of press.
        _targetWindow = foregroundWindow;
        try
        {
            _capture.Start();
            State = DictationState.Recording;
            _overlay.SetState(OverlayState.Recording);
            _logger.Info("Recording started", new() { ["hwnd"] = foregroundWindow.ToInt64() });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start recording", new() { ["error"] = ex.Message });
            _overlay.ShowError("Can't access the microphone");
            State = DictationState.Idle;
        }
    }

    /// <summary>Stop recording, transcribe, and paste into the captured window. UI thread.</summary>
    public async Task HandleReleasedAsync()
    {
        if (State != DictationState.Recording)
        {
            _logger.Debug("Hotkey release ignored — not recording", new() { ["state"] = State.ToString() });
            return;
        }

        State = DictationState.Transcribing;

        AudioClip? clip;
        try
        {
            clip = await _capture.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to stop capture", new() { ["error"] = ex.Message });
            _overlay.ShowError("Recording failed");
            State = DictationState.Idle;
            return;
        }

        if (clip is null || clip.WavBytes.Length == 0)
        {
            _logger.Warning("No audio captured");
            _overlay.SetState(OverlayState.Hidden);
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
            _overlay.ShowError("Couldn't transcribe — recording saved");
            State = DictationState.Idle;
            return;
        }

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
        State = DictationState.Idle;
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
}
