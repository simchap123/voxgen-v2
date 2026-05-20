using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VoxGen.Desktop.Transcription;

namespace VoxGen.Desktop.Audio;

/// <summary>A selectable microphone. <see cref="Id"/> is the stable identifier (PRD §11 — authoritative); <see cref="Name"/> is display-only.</summary>
public sealed record AudioDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class AudioLevelEventArgs : EventArgs
{
    /// <summary>Normalized 0..1 input level. Drives the future v1-faithful waveform (unused this slice).</summary>
    public float Level { get; }
    public AudioLevelEventArgs(float level) => Level = level;
}

/// <summary>
/// Microphone capture (PRD §8.4). Lifecycle: <see cref="Initialize"/> warm-opens the device
/// (Approach 1 — no capture, mic light stays off), then <see cref="Start"/> begins capture on a
/// hotkey press and <see cref="StopAsync"/> ends it and returns the WAV. Re-call <see cref="Initialize"/>
/// to switch devices.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Warm-open <paramref name="deviceId"/> (or the system default when null/empty). Idempotent; switching devices is allowed when not recording.</summary>
    void Initialize(string? deviceId);

    /// <summary>Begin capturing. Must be cheap (device already warm). Throws if no device is initialized.</summary>
    void Start();

    /// <summary>Stop capturing and return the recording as a WAV <see cref="AudioClip"/>, or null if nothing was captured.</summary>
    Task<AudioClip?> StopAsync();

    /// <summary>
    /// Returns the audio captured so far this recording as a 16 kHz mono WAV, WITHOUT stopping —
    /// for live/streaming transcription. Null if not recording or nothing captured yet. Thread-safe.
    /// </summary>
    byte[]? SnapshotWav();

    bool IsRecording { get; }

    /// <summary>Fires periodically with the normalized input level while recording.</summary>
    event EventHandler<AudioLevelEventArgs>? LevelChanged;
}

/// <summary>Enumerates capture devices for the Settings mic picker (PRD §8.9).</summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDevice> GetCaptureDevices();
    AudioDevice? GetDefaultCaptureDevice();
}
