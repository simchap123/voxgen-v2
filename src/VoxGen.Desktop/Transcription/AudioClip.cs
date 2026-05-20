using System;

namespace VoxGen.Desktop.Transcription;

/// <summary>
/// A complete WAV recording ready to be transcribed. PRD §8.4 — audio is sent to the
/// backend as WAV (no encoder dependency). The audio file's lifecycle on disk is owned
/// by the caller; this record only carries the bytes for transmission.
/// </summary>
public sealed record AudioClip
{
    public required byte[] WavBytes { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required TimeSpan Duration { get; init; }
}
