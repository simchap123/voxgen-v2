using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Clipboard;
using VoxGen.Desktop.Core;
using VoxGen.Desktop.Overlay;
using VoxGen.Desktop.Transcription;
using Xunit;

namespace VoxGen.Desktop.Tests.Core;

public sealed class DictationControllerTests
{
    private static readonly IntPtr Hwnd = new(0x1234);

    private static AudioClip SampleClip() => new()
    {
        WavBytes = new byte[] { 1, 2, 3, 4 },
        SampleRate = 16000,
        Channels = 1,
        Duration = TimeSpan.FromSeconds(2),
    };

    private static DictationController Build(
        FakeAudioCapture capture,
        FakeProvider provider,
        FakePaste paste,
        FakeOverlay overlay,
        bool paused = false,
        List<AudioClip>? preserved = null)
        => new(capture, provider, paste, overlay, () => paused, new TestLogger(),
               clip => { preserved?.Add(clip); return "fake.wav"; });

    [Fact]
    public void Pressed_when_idle_starts_capture_and_shows_recording()
    {
        var cap = new FakeAudioCapture(); var prov = new FakeProvider();
        var paste = new FakePaste(); var overlay = new FakeOverlay();
        var c = Build(cap, prov, paste, overlay);

        c.HandlePressed(Hwnd);

        Assert.Equal(DictationState.Recording, c.State);
        Assert.Equal(1, cap.StartCalls);
        Assert.Equal(OverlayState.Recording, overlay.LastState);
    }

    [Fact]
    public void Pressed_when_paused_is_ignored()
    {
        var cap = new FakeAudioCapture(); var overlay = new FakeOverlay();
        var c = Build(cap, new FakeProvider(), new FakePaste(), overlay, paused: true);

        c.HandlePressed(Hwnd);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(0, cap.StartCalls);
    }

    [Fact]
    public void Pressed_when_capture_throws_shows_error_and_stays_idle()
    {
        var cap = new FakeAudioCapture { ThrowOnStart = true };
        var overlay = new FakeOverlay();
        var c = Build(cap, new FakeProvider(), new FakePaste(), overlay);

        c.HandlePressed(Hwnd);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.NotNull(overlay.LastError);
    }

    [Fact]
    public async Task Happy_path_transcribes_and_pastes_into_captured_window()
    {
        var cap = new FakeAudioCapture { ClipToReturn = SampleClip() };
        var prov = new FakeProvider { Result = Result("hello world") };
        var paste = new FakePaste { Outcome = PasteOutcome.Pasted };
        var overlay = new FakeOverlay();
        var c = Build(cap, prov, paste, overlay);

        c.HandlePressed(Hwnd);
        await c.HandleReleasedAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(1, cap.StopCalls);
        Assert.Equal(1, prov.Calls);
        Assert.Equal(Hwnd, paste.LastTarget);
        Assert.Equal("hello world", paste.LastText);
        Assert.Equal(OverlayState.Hidden, overlay.LastState);
    }

    [Fact]
    public async Task Transcription_failure_preserves_recording_and_does_not_paste()
    {
        var cap = new FakeAudioCapture { ClipToReturn = SampleClip() };
        var prov = new FakeProvider { Throw = true };
        var paste = new FakePaste();
        var overlay = new FakeOverlay();
        var preserved = new List<AudioClip>();
        var c = Build(cap, prov, paste, overlay, preserved: preserved);

        c.HandlePressed(Hwnd);
        await c.HandleReleasedAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Single(preserved);                 // §13 — recording kept
        Assert.Equal(0, paste.PasteCalls);         // nothing pasted on failure
        Assert.NotNull(overlay.LastError);
    }

    [Fact]
    public async Task Paste_failure_surfaces_error_but_completes()
    {
        var cap = new FakeAudioCapture { ClipToReturn = SampleClip() };
        var prov = new FakeProvider { Result = Result("text") };
        var paste = new FakePaste { Outcome = PasteOutcome.LeftOnClipboard, Error = "clipboard locked" };
        var overlay = new FakeOverlay();
        var c = Build(cap, prov, paste, overlay);

        c.HandlePressed(Hwnd);
        await c.HandleReleasedAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.NotNull(overlay.LastError);         // §8.8 — user told text is on clipboard
    }

    [Fact]
    public async Task No_audio_captured_returns_to_idle_without_transcribing()
    {
        var cap = new FakeAudioCapture { ClipToReturn = null };
        var prov = new FakeProvider();
        var c = Build(cap, prov, new FakePaste(), new FakeOverlay());

        c.HandlePressed(Hwnd);
        await c.HandleReleasedAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(0, prov.Calls);
    }

    [Fact]
    public async Task Released_without_recording_is_ignored()
    {
        var prov = new FakeProvider();
        var c = Build(new FakeAudioCapture(), prov, new FakePaste(), new FakeOverlay());

        await c.HandleReleasedAsync();   // never pressed

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(0, prov.Calls);
    }

    private static TranscriptionResult Result(string text) => new()
    {
        FinalText = text, RawText = text, Language = "en",
        Duration = TimeSpan.FromSeconds(2), CleanupApplied = false,
    };

    // ---- fakes ----

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public int StartCalls; public int StopCalls;
        public bool ThrowOnStart;
        public AudioClip? ClipToReturn;
        public bool IsRecording { get; private set; }
        public event EventHandler<AudioLevelEventArgs>? LevelChanged;

        public void Initialize(string? deviceId) { }
        public void Start()
        {
            if (ThrowOnStart) throw new InvalidOperationException("no device");
            StartCalls++; IsRecording = true;
            LevelChanged?.Invoke(this, new AudioLevelEventArgs(0f)); // touch event to satisfy analyzer
        }
        public Task<AudioClip?> StopAsync() { StopCalls++; IsRecording = false; return Task.FromResult(ClipToReturn); }
        public void Dispose() { }
    }

    private sealed class FakeProvider : ITranscriptionProvider
    {
        public int Calls; public bool Throw; public TranscriptionResult? Result;
        public Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("backend down");
            return Task.FromResult(Result ?? new TranscriptionResult { FinalText = "" });
        }
    }

    private sealed class FakePaste : IClipboardPaste
    {
        public int PasteCalls; public IntPtr LastTarget; public string? LastText;
        public PasteOutcome Outcome = PasteOutcome.Pasted; public string? Error;
        public PasteResult PasteInto(IntPtr targetWindow, string text)
        {
            PasteCalls++; LastTarget = targetWindow; LastText = text;
            return new PasteResult(Outcome, Error);
        }
    }

    private sealed class FakeOverlay : IRecordingOverlay
    {
        public OverlayState LastState = OverlayState.Hidden;
        public string? LastError;
        public void SetState(OverlayState state) => LastState = state;
        public void ShowError(string message) => LastError = message;
    }
}
