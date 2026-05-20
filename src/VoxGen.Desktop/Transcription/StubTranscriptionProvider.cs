using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// Dev-only transcription provider — returns canned text after a simulated round-trip delay.
/// Used while the real backend (PRD §9) doesn't exist or isn't configured, so the full local
/// loop (hotkey → record → "transcribe" → paste) can be tested with zero credentials. It echoes
/// the capture facts so a successful recording is visible in the pasted text. Never shipped.
/// </summary>
public sealed class StubTranscriptionProvider : ITranscriptionProvider
{
    private readonly TimeSpan _simulatedLatency;

    public StubTranscriptionProvider(TimeSpan? simulatedLatency = null)
        => _simulatedLatency = simulatedLatency ?? TimeSpan.FromMilliseconds(800);

    public async Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        await Task.Delay(_simulatedLatency, ct).ConfigureAwait(false);

        var channels = audio.Channels == 1 ? "mono" : $"{audio.Channels}ch";
        var text = $"This is a VoxGen test transcript. " +
                   $"[stub: {audio.Duration.TotalSeconds:0.0}s, {audio.SampleRate / 1000}kHz {channels}]";

        return new TranscriptionResult
        {
            FinalText = text,
            RawText = text,
            Language = "en",
            Duration = audio.Duration,
            CleanupApplied = false,
        };
    }
}
