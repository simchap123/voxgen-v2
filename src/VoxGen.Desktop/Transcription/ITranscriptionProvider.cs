using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// All transcription goes through this interface (PRD §6.3). v1 ships exactly one
/// implementation — <see cref="VoxGenManagedProvider"/> — but the interface stays so a
/// BYOK provider can be added later without rework. Nothing else in the app (recording,
/// hotkeys, paste, history, overlay) knows which provider is wired up.
/// </summary>
public interface ITranscriptionProvider
{
    Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct);
}
