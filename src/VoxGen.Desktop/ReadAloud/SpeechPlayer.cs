using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.ReadAloud;

public enum SpeechState
{
    Idle,
    Loading,
    Playing,
    Paused,
}

/// <summary>
/// Plays read-aloud text: chunks it, synthesizes each chunk with Groq (prefetching the next while
/// the current one plays), and supports pause/resume, stop, and live speed changes.
///
/// Speed: Groq's voice has no rate parameter, so playback is time-stretched locally. The source is
/// played "as if" recorded at rate × its sample rate (faster + higher), then pitch-shifted back by
/// 1/rate so the voice keeps its natural pitch, then resampled to 48 kHz for the device. A speed
/// change mid-sentence rebuilds that chain on the same reader, so it applies within ~a word.
///
/// Thread-safety: public methods may be called from any thread. <see cref="StateChanged"/> fires on
/// a background thread — UI subscribers marshal themselves.
/// </summary>
public sealed class SpeechPlayer : IDisposable
{
    public const double MinRate = 0.5;
    public const double MaxRate = 2.5;
    public const double RateStep = 0.25;
    private const int OutputSampleRate = 48000;

    private readonly Func<ISpeechSynthesizer> _engine;
    private readonly Func<string> _voice;
    private readonly Action<string> _reportError;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private CancellationTokenSource? _sessionCts;
    private WaveOutEvent? _output;
    private TaskCompletionSource? _rateChanged;
    private double _rate;
    private bool _paused;

    /// <param name="engine">Read per session so switching NVIDIA/Groq in Settings applies to the next read.</param>
    public SpeechPlayer(Func<ISpeechSynthesizer> engine, Func<string> voice, double initialRate, Action<string> reportError, ILogger logger)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rate = ClampRate(initialRate);
    }

    /// <summary>Fires on every state, rate or text change.</summary>
    public event EventHandler? StateChanged;

    public SpeechState State { get; private set; } = SpeechState.Idle;
    public double Rate { get { lock (_lock) return _rate; } }

    /// <summary>The full text currently being read (for Copy).</summary>
    public string? CurrentText { get; private set; }

    public static double ClampRate(double rate) => Math.Round(Math.Clamp(rate, MinRate, MaxRate) / RateStep) * RateStep;

    /// <summary>Starts reading <paramref name="text"/>, interrupting anything already playing.</summary>
    public void Speak(string text)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _sessionCts?.Cancel();
            _sessionCts = cts = new CancellationTokenSource();
            _paused = false;
            CurrentText = text;
        }
        SetState(SpeechState.Loading);
        _ = Task.Run(() => RunSessionAsync(text, cts.Token));
    }

    public void Stop()
    {
        lock (_lock)
        {
            _sessionCts?.Cancel();
            _sessionCts = null;
            _paused = false;
            try { _output?.Stop(); } catch { /* already stopped */ }
        }
        SetState(SpeechState.Idle);
    }

    public void TogglePause()
    {
        lock (_lock)
        {
            if (_output is null || State is SpeechState.Idle) return;
            _paused = !_paused;
            if (_paused) _output.Pause();
            else _output.Play();
        }
        SetState(_paused ? SpeechState.Paused : SpeechState.Playing);
    }

    public void Faster() => SetRate(Rate + RateStep);
    public void Slower() => SetRate(Rate - RateStep);

    public void SetRate(double rate)
    {
        lock (_lock)
        {
            var clamped = ClampRate(rate);
            if (Math.Abs(clamped - _rate) < 0.001) return;
            _rate = clamped;
            _rateChanged?.TrySetResult();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(SpeechState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunSessionAsync(string text, CancellationToken ct)
    {
        var speech = _engine();
        var chunks = GroqSpeech.ChunkText(text, speech.MaxCharsPerRequest, totalCap: 4000);
        if (chunks.Count == 0) { FinishIfCurrent(ct); return; }
        _logger.Info("Read aloud", new() { ["chars"] = text.Length, ["chunks"] = chunks.Count });

        var voice = _voice();
        try
        {
            var next = speech.SynthesizeAsync(chunks[0], voice, ct);
            for (var i = 0; i < chunks.Count; i++)
            {
                var wav = await next.ConfigureAwait(false);
                if (i + 1 < chunks.Count) next = speech.SynthesizeAsync(chunks[i + 1], voice, ct);
                await PlayChunkAsync(wav, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // superseded or stopped — the new owner manages state
        }
        catch (Exception ex)
        {
            _logger.Warning("Read aloud failed", new() { ["error"] = ex.Message });
            _reportError(ex.Message);
        }
        FinishIfCurrent(ct);
    }

    private void FinishIfCurrent(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        SetState(SpeechState.Idle);
    }

    private async Task PlayChunkAsync(byte[] wav, CancellationToken ct)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var source = reader.ToSampleProvider();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double rate;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new WaveOutEvent { DesiredLatency = 150 };

            lock (_lock)
            {
                rate = _rate;
                _rateChanged = rateChanged;
                output.PlaybackStopped += (_, _) => finished.TrySetResult();
                output.Init(BuildChain(source, rate));
                _output = output;
                if (!_paused) output.Play();
            }
            if (!_paused) SetState(SpeechState.Playing);

            try
            {
                using (ct.Register(() => { try { output.Stop(); } catch { } }))
                {
                    var done = await Task.WhenAny(finished.Task, rateChanged.Task).ConfigureAwait(false);
                    if (done == finished.Task) break; // chunk played out (or stopped)

                    // Speed changed: stop this chain, rewind by roughly what the device had buffered,
                    // and rebuild at the new rate from (about) the same spot.
                    lock (_lock)
                    {
                        output.Stop();
                        var rewind = (long)(reader.WaveFormat.AverageBytesPerSecond * 0.2 * rate);
                        rewind -= rewind % reader.WaveFormat.BlockAlign;
                        reader.Position = Math.Max(0, reader.Position - rewind);
                    }
                    await finished.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_output, output)) _output = null;
                    _rateChanged = null;
                }
                output.Dispose();
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>source → (faster/slower, same pitch) → mono 48 kHz float.</summary>
    private static ISampleProvider BuildChain(ISampleProvider source, double rate)
    {
        ISampleProvider chain = source;
        if (Math.Abs(rate - 1.0) > 0.001)
        {
            chain = new RateRelabel(source, rate);
            chain = new SmbPitchShiftingSampleProvider(chain) { PitchFactor = (float)(1.0 / rate) };
        }
        if (chain.WaveFormat.SampleRate != OutputSampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, OutputSampleRate);
        }
        return chain;
    }

    /// <summary>Passes samples through unchanged but reports sampleRate × rate, so downstream
    /// resampling consumes the source faster (rate &gt; 1) or slower (rate &lt; 1).</summary>
    private sealed class RateRelabel : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public RateRelabel(ISampleProvider source, double rate)
        {
            _source = source;
            var f = source.WaveFormat;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)Math.Round(f.SampleRate * rate), f.Channels);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
    }

    public void Dispose() => Stop();
}
