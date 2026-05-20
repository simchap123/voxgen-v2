using System;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Backend;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// v1's only transcription provider (PRD §6.3, §8.6). Forwards the WAV to the VoxGen
/// managed backend, which holds the real keys (PRD §6.4, §9.1).
///
/// Refresh-on-401 is handled by the injected <see cref="_getAccessTokenAsync"/> delegate:
/// the caller (an auth manager) is the one place that owns refresh tokens and can decide
/// whether to refresh or prompt re-login. This provider just asks for a token, calls the
/// backend, and — on a single 401 — asks once more (the delegate is expected to refresh).
///
/// Never deletes the user's audio. PRD §5.3 / §8.4 — the caller decides when (after a
/// successful paste, or when the user dismisses an error) the temp file goes away.
/// </summary>
public sealed class VoxGenManagedProvider : ITranscriptionProvider
{
    private readonly VoxGenBackendClient _backend;
    private readonly Func<CancellationToken, Task<string>> _getAccessTokenAsync;
    private readonly ILogger _logger;
    private readonly TranscriptionOptions _defaultOptions;

    public VoxGenManagedProvider(
        VoxGenBackendClient backend,
        Func<CancellationToken, Task<string>> getAccessTokenAsync,
        ILogger logger,
        TranscriptionOptions defaultOptions)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _getAccessTokenAsync = getAccessTokenAsync ?? throw new ArgumentNullException(nameof(getAccessTokenAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultOptions = defaultOptions ?? throw new ArgumentNullException(nameof(defaultOptions));
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));

        var attempt = 0;
        while (true)
        {
            attempt++;
            string token;
            try
            {
                token = await _getAccessTokenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to obtain access token before transcription", new()
                {
                    ["error"] = ex.Message,
                });
                throw;
            }

            try
            {
                var raw = await _backend
                    .TranscribeAsync(audio.WavBytes, token, _defaultOptions, ct)
                    .ConfigureAwait(false);

                return new TranscriptionResult
                {
                    FinalText = raw.FinalText,
                    RawText = raw.RawText,
                    Language = raw.Language,
                    Duration = audio.Duration,
                    CleanupApplied = raw.CleanupApplied,
                };
            }
            catch (UnauthenticatedException) when (attempt == 1)
            {
                // First 401 — give the token delegate a chance to refresh and try again.
                // The delegate is the only place that knows whether the refresh actually
                // produced a new token vs. a re-login is needed. If the second attempt
                // still 401s, we let it bubble.
                _logger.Info("Transcription got 401; retrying once after token refresh");
            }
        }
    }
}
