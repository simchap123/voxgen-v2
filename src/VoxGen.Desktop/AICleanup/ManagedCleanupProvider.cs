using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.AICleanup;

/// <summary>
/// Throw-only placeholder. In v1 cleanup is performed server-side as part of
/// <see cref="Backend.VoxGenBackendClient.TranscribeAsync"/> (PRD §8.7, §9.2) — there is
/// no client-side cleanup path. This type exists so that DI containers / composition
/// roots fail loudly if someone wires <see cref="ICleanupProvider"/> into the v1 pipeline
/// by mistake, rather than silently producing wrong output.
///
/// Inject an <see cref="ICleanupProvider"/> only when adding BYOK.
/// </summary>
public sealed class ManagedCleanupProvider : ICleanupProvider
{
    public Task<string> CleanupAsync(string rawText, CleanupOptions options, CancellationToken ct) =>
        throw new NotSupportedException(
            "Managed cleanup is performed server-side as part of transcription in v1. " +
            "Inject ICleanupProvider only when adding BYOK.");
}
