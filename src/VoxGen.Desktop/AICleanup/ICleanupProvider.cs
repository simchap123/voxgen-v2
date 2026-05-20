using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.AICleanup;

/// <summary>
/// Per-request options for AI cleanup. Kept minimal in v1 because the cleanup happens
/// server-side as part of the transcription request (PRD §8.7) — this interface is the
/// vestigial seam for BYOK-cleanup later.
/// </summary>
public sealed record CleanupOptions
{
    public static readonly CleanupOptions Defaults = new();
}

/// <summary>
/// Future BYOK-cleanup seam.
///
/// In v1 cleanup is performed by the backend as part of <c>VoxGenBackendClient.TranscribeAsync</c>
/// (PRD §8.7, §9.2) — there is no client-side cleanup code path. This interface is kept
/// so that the architecture mirrors <see cref="Transcription.ITranscriptionProvider"/>
/// (PRD §6.3) and a BYOK build can wire in <c>OpenAiCleanupProvider</c>-style
/// implementations later without touching the surrounding pipeline.
///
/// Nothing in v1 calls this interface. <see cref="ManagedCleanupProvider"/> throws to
/// make that explicit if anything ever does.
/// </summary>
public interface ICleanupProvider
{
    Task<string> CleanupAsync(string rawText, CleanupOptions options, CancellationToken ct);
}
