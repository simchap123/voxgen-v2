using System;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// Final transcription output as the rest of the app sees it. <see cref="FinalText"/> is
/// what gets pasted; <see cref="RawText"/> is the pre-cleanup transcript (kept for local
/// history if enabled — PRD §8.11).
/// </summary>
public sealed record TranscriptionResult
{
    public required string FinalText { get; init; }
    public string? RawText { get; init; }
    public string? Language { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>True if the backend ran AI cleanup on the raw transcript (PRD §8.7).</summary>
    public bool CleanupApplied { get; init; }
}
