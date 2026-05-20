using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Logging;
using Whisper.net;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// LOCAL PREVIEW (v2.0.0-preview) — on-device transcription via Whisper <c>tiny.en</c> (whisper.cpp
/// through Whisper.net), so testers get real transcription with no account or cloud key until
/// <see cref="VoxGenManagedProvider"/> + the managed backend exist. PRD §2/§20 keep local models out
/// of scope for GA and §6.2 needs package approval — both deliberately waived for the preview per
/// PRD §3.4. Lives behind the <see cref="ITranscriptionProvider"/> seam and is auto-swapped for the
/// managed provider once BackendConfig is real.
///
/// tiny.en is the smallest/fastest Whisper model (~75 MB). Input is the 16 kHz mono WAV that
/// <c>NAudioCapture</c> already produces — exactly what whisper.cpp wants, so no resampling here.
/// </summary>
public sealed class WhisperLocalTranscriptionProvider : ITranscriptionProvider, IDisposable
{
    // Canonical ggml tiny.en weights (whisper.cpp). Downloaded once, then fully offline.
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin";

    private readonly string _modelPath;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly int _threads = Math.Max(1, Environment.ProcessorCount);

    private WhisperFactory? _factory;
    private bool _disposed;

    public WhisperLocalTranscriptionProvider(string modelDirectory, ILogger logger, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(modelDirectory);
        Directory.CreateDirectory(modelDirectory);
        _modelPath = Path.Combine(modelDirectory, "ggml-tiny.en.bin");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Download (first run) + load the model up front so the first hotkey press is fast.</summary>
    public Task WarmUpAsync(CancellationToken ct = default) => EnsureReadyAsync(ct);

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_factory is not null) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_factory is not null) return;

            if (!File.Exists(_modelPath) || new FileInfo(_modelPath).Length == 0)
            {
                _logger.Info("Downloading Whisper tiny.en model (first run)", new() { ["path"] = _modelPath });
                await DownloadModelAsync(ct).ConfigureAwait(false);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(_modelPath);
            _logger.Info("Whisper model loaded", new() { ["ms"] = sw.ElapsedMilliseconds, ["threads"] = _threads });
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        var tmp = _modelPath + ".tmp";
        using (var resp = await _http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _modelPath, overwrite: true);
        _logger.Info("Whisper model downloaded", new() { ["bytes"] = new FileInfo(_modelPath).Length });
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();

        using var ms = new MemoryStream(audio.WavBytes, writable: false);
        using var processor = _factory!.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(_threads)
            .Build();

        await foreach (var segment in processor.ProcessAsync(ms).WithCancellation(ct).ConfigureAwait(false))
        {
            sb.Append(segment.Text);
        }

        var text = sb.ToString().Trim();
        _logger.Info("Whisper transcribed", new()
        {
            ["audioMs"] = (long)audio.Duration.TotalMilliseconds,
            ["transcribeMs"] = sw.ElapsedMilliseconds,
            ["chars"] = text.Length,
        });

        return new TranscriptionResult
        {
            FinalText = text,
            RawText = text,
            Language = "en",
            Duration = audio.Duration,
            CleanupApplied = false,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _factory?.Dispose();
        _initGate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
