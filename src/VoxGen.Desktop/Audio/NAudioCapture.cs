using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Transcription;

namespace VoxGen.Desktop.Audio;

/// <summary>
/// NAudio/WASAPI implementation of <see cref="IAudioCapture"/> (PRD §8.4).
///
/// TARGET OUTPUT FORMAT — 16 kHz, mono, 16-bit PCM. This is the Whisper-optimal format and the
/// smallest reasonable payload to upload, so we always resample the device's native capture
/// format down to it before building the WAV.
///
/// LIFECYCLE / MIC-LIGHT INVARIANT (CLAUDE.md invariant 5, PRD §8.4, §14.1) — read before editing:
///
///   Initialize(deviceId)  →  resolve the MMDevice and CONSTRUCT a WasapiCapture. NAudio's
///                            WasapiCapture activates/initializes the underlying WASAPI AudioClient
///                            lazily inside StartRecording() (its private InitializeCaptureDevice),
///                            NOT in the constructor. The constructor only stores the device and
///                            reads its mixer WaveFormat. Therefore constructing here warms the
///                            device WITHOUT turning on the Windows "microphone in use" indicator.
///   Start()               →  StartRecording(). THIS is the call that activates the AudioClient and
///                            lights the mic indicator. It is cheap because the device object already
///                            exists, so a hotkey press starts buffering within the <100 ms budget.
///   StopAsync()           →  StopRecording(), wait for the RecordingStopped callback, assemble the
///                            captured PCM, resample to 16k/mono/16-bit, wrap in a WAV, return.
///
/// Re-calling Initialize while not recording disposes the old capture and warms the new device
/// (mic picker change). Re-initializing while recording is rejected.
///
/// THREADING — DataAvailable / RecordingStopped fire on NAudio's capture thread. The PCM
/// accumulation buffer and capture-state fields are guarded by <see cref="_sync"/>.
///
/// RESAMPLING CHOICE — WdlResamplingSampleProvider. It is fully managed (no Media Foundation
/// dependency, no COM init concerns on the capture thread) and outputs float samples that
/// SampleToWaveProvider16 converts straight to the 16-bit PCM we need. Quality is more than
/// adequate for speech at 16 kHz; MediaFoundationResampler's extra fidelity buys nothing here.
/// </summary>
public sealed class NAudioCapture : IAudioCapture
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1;
    private const int TargetBitsPerSample = 16;

    private readonly ILogger _logger;
    private readonly object _sync = new();

    // Warmed device + capture client. Created in Initialize, disposed in Initialize(switch)/Dispose.
    private WasapiCapture? _capture;
    private string? _deviceId;
    private string? _deviceName;

    // Accumulates raw device-native PCM bytes for the current recording. Guarded by _sync.
    private MemoryStream? _pcmBuffer;
    private WaveFormat? _captureFormat;
    private bool _isRecording;
    private bool _disposed;

    // Signalled by the RecordingStopped callback so StopAsync can await a clean teardown.
    private TaskCompletionSource<bool>? _stopCompletion;

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public bool IsRecording
    {
        get { lock (_sync) { return _isRecording; } }
    }

    public NAudioCapture(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Warm-open the device. Constructs the WasapiCapture (no stream, mic light stays off).
    /// Idempotent for the same id; switches devices when called with a different id while idle.
    /// </summary>
    public void Initialize(string? deviceId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_isRecording)
                throw new InvalidOperationException("Cannot re-initialize the capture device while recording.");

            MMDevice device = ResolveDevice(deviceId);
            try
            {
                // Already warmed on this exact endpoint — nothing to do.
                if (_capture is not null && string.Equals(_deviceId, device.ID, StringComparison.Ordinal))
                {
                    device.Dispose();
                    return;
                }

                DisposeCapture();

                // Constructing WasapiCapture does NOT start the stream and does NOT light the mic
                // indicator — the AudioClient is only activated in StartRecording(). See the class remarks.
                var capture = new WasapiCapture(device, useEventSync: true);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;

                _capture = capture;
                _deviceId = device.ID;
                _deviceName = device.FriendlyName;
                _captureFormat = capture.WaveFormat;

                _logger.Info("Capture device warmed", new()
                {
                    ["deviceId"] = _deviceId,
                    ["deviceName"] = _deviceName,
                    ["nativeSampleRate"] = _captureFormat.SampleRate,
                    ["nativeChannels"] = _captureFormat.Channels,
                    ["nativeBits"] = _captureFormat.BitsPerSample,
                    ["nativeEncoding"] = _captureFormat.Encoding.ToString(),
                });
            }
            finally
            {
                // WasapiCapture keeps its own reference to the device; release ours either way.
                device.Dispose();
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_capture is null)
                throw new InvalidOperationException("Initialize must be called before Start.");
            if (_isRecording)
                return; // already capturing — treat as idempotent

            _pcmBuffer = new MemoryStream();
            _stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isRecording = true;

            // This is the moment the WASAPI AudioClient is activated and the mic indicator lights up.
            _capture.StartRecording();
        }
        _logger.Debug("Recording started", new() { ["deviceId"] = _deviceId });
    }

    public async Task<AudioClip?> StopAsync()
    {
        WasapiCapture? capture;
        Task stopTask;
        lock (_sync)
        {
            if (!_isRecording || _capture is null)
            {
                _logger.Debug("StopAsync called while not recording");
                return null;
            }
            capture = _capture;
            stopTask = _stopCompletion?.Task ?? Task.CompletedTask;
            // StopRecording is asynchronous; the buffer is finalized in the RecordingStopped callback.
            capture.StopRecording();
        }

        // Await the RecordingStopped callback (off the lock) so we don't assemble a partial buffer.
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("RecordingStopped reported an error", new() { ["error"] = ex.Message });
        }

        byte[] nativePcm;
        WaveFormat? captureFormat;
        lock (_sync)
        {
            _isRecording = false;
            nativePcm = _pcmBuffer?.ToArray() ?? Array.Empty<byte>();
            captureFormat = _captureFormat;
            _pcmBuffer?.Dispose();
            _pcmBuffer = null;
            _stopCompletion = null;
        }

        if (nativePcm.Length == 0 || captureFormat is null)
        {
            _logger.Info("Recording produced no audio");
            return null;
        }

        try
        {
            byte[] wav = BuildWav(nativePcm, captureFormat, out TimeSpan duration);
            _logger.Info("Recording assembled", new()
            {
                ["nativeBytes"] = nativePcm.Length,
                ["wavBytes"] = wav.Length,
                ["durationMs"] = (int)duration.TotalMilliseconds,
            });
            return new AudioClip
            {
                WavBytes = wav,
                SampleRate = TargetSampleRate,
                Channels = TargetChannels,
                Duration = duration,
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to assemble WAV from captured audio", new() { ["error"] = ex.Message });
            return null;
        }
    }

    public byte[]? SnapshotWav()
    {
        byte[] nativePcm;
        WaveFormat? captureFormat;
        lock (_sync)
        {
            if (_disposed || !_isRecording || _pcmBuffer is null || _captureFormat is null) return null;
            // Cheap copy of the in-progress buffer; BuildWav is static/pure so we run it off the lock.
            nativePcm = _pcmBuffer.ToArray();
            captureFormat = _captureFormat;
        }

        if (nativePcm.Length == 0) return null;
        try
        {
            return BuildWav(nativePcm, captureFormat, out _);
        }
        catch (Exception ex)
        {
            _logger.Warning("Snapshot WAV build failed", new() { ["error"] = ex.Message });
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // If we're mid-recording, stop synchronously; we won't await the callback on dispose.
                if (_isRecording && _capture is not null)
                {
                    try { _capture.StopRecording(); } catch { /* best effort */ }
                }
            }
            finally
            {
                _isRecording = false;
                DisposeCapture();
                _pcmBuffer?.Dispose();
                _pcmBuffer = null;
                _stopCompletion?.TrySetResult(true);
                _stopCompletion = null;
            }
        }
    }

    // ---------- capture callbacks (NAudio thread) ----------

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            _pcmBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // Compute a coarse normalized level for the future waveform (PRD §8.10). Done outside the
        // lock-held write above to keep the buffer lock short; the format read is cheap.
        WaveFormat? format = _captureFormat;
        if (format is not null && e.BytesRecorded > 0)
        {
            float level = ComputeLevel(e.Buffer, e.BytesRecorded, format);
            // Subscribers (overlay/UI) must marshal to their own thread; we fire on the capture thread.
            try { LevelChanged?.Invoke(this, new AudioLevelEventArgs(level)); }
            catch (Exception ex) { _logger.Error("LevelChanged handler raised", new() { ["error"] = ex.Message }); }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (e.Exception is not null) _stopCompletion?.TrySetException(e.Exception);
            else _stopCompletion?.TrySetResult(true);
        }
    }

    // ---------- helpers ----------

    private MMDevice ResolveDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    return enumerator.GetDevice(deviceId);
                }
                catch (Exception ex)
                {
                    // The persisted device may have been unplugged — fall back to the default.
                    _logger.Warning("Requested capture device not found; falling back to default",
                        new() { ["deviceId"] = deviceId, ["error"] = ex.Message });
                }
            }

            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                throw new InvalidOperationException("No active capture device is available.");

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Resample/convert the native-format PCM to 16 kHz mono 16-bit and wrap it in a WAV.
    /// When the device already captures at the target format we skip resampling.
    /// </summary>
    private static byte[] BuildWav(byte[] nativePcm, WaveFormat captureFormat, out TimeSpan duration)
    {
        byte[] targetPcm;

        bool alreadyTarget =
            captureFormat.SampleRate == TargetSampleRate &&
            captureFormat.Channels == TargetChannels &&
            captureFormat.BitsPerSample == TargetBitsPerSample &&
            captureFormat.Encoding == WaveFormatEncoding.Pcm;

        if (alreadyTarget)
        {
            targetPcm = nativePcm;
        }
        else
        {
            // Wrap the raw captured bytes as a stream in the device's native format, take it to a
            // float SampleProvider, resample to 16 kHz, force mono, then convert to 16-bit PCM.
            using var rawStream = new RawSourceWaveStream(
                new MemoryStream(nativePcm, writable: false), captureFormat);

            ISampleProvider samples = rawStream.ToSampleProvider();
            if (samples.WaveFormat.Channels != TargetChannels)
            {
                // ToMono averages channels; only valid path we need is stereo/multi -> mono.
                samples = samples.ToMono();
            }

            var resampler = new WdlResamplingSampleProvider(samples, TargetSampleRate);
            var to16Bit = new SampleToWaveProvider16(resampler);

            using var outPcm = new MemoryStream();
            var readBuffer = new byte[to16Bit.WaveFormat.AverageBytesPerSecond];
            int read;
            while ((read = to16Bit.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                outPcm.Write(readBuffer, 0, read);
            }
            targetPcm = outPcm.ToArray();
        }

        int bytesPerSample = TargetBitsPerSample / 8;
        int frameCount = targetPcm.Length / (bytesPerSample * TargetChannels);
        duration = TimeSpan.FromSeconds((double)frameCount / TargetSampleRate);

        return WavWriter.Write(targetPcm, TargetSampleRate, TargetChannels, TargetBitsPerSample);
    }

    /// <summary>
    /// Coarse normalized 0..1 peak level from a native capture buffer. Handles the two formats
    /// WASAPI shared-mode capture realistically yields: 32-bit IEEE float and 16-bit PCM.
    /// </summary>
    private static float ComputeLevel(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        float peak = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                float abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float abs = Math.Abs(sample / 32768f);
                if (abs > peak) peak = abs;
            }
        }
        // Unknown formats: leave peak at 0 — the waveform just shows quiet, which is harmless.

        return peak > 1f ? 1f : peak;
    }

    private void DisposeCapture()
    {
        if (_capture is null) return;
        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning("Disposing capture device raised", new() { ["error"] = ex.Message });
        }
        finally
        {
            _capture = null;
            _captureFormat = null;
            _deviceId = null;
            _deviceName = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NAudioCapture));
    }
}
