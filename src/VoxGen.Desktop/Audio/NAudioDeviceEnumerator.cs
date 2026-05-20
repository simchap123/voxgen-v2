using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Audio;

/// <summary>
/// WASAPI-backed capture-device enumeration for the Settings mic picker (PRD §8.9).
///
/// Uses NAudio's <see cref="MMDeviceEnumerator"/> rather than the legacy WaveIn device index
/// API deliberately: <see cref="MMDevice.ID"/> is a stable endpoint string that survives
/// replug and reboot, which is what PRD §11 requires for the persisted microphone setting.
/// (WaveIn indices renumber as devices come and go and are therefore unsafe to persist.)
///
/// Enumeration must never throw — a flaky audio stack should degrade to "no devices", not crash
/// the Settings window. Errors are logged and swallowed.
/// </summary>
public sealed class NAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger _logger;

    public NAudioDeviceEnumerator(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<AudioDevice> GetCaptureDevices()
    {
        var devices = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // Active capture endpoints only — disabled/unplugged endpoints aren't selectable.
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioDevice { Id = device.ID, Name = device.FriendlyName });
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enumerate capture devices", new() { ["error"] = ex.Message });
        }

        return devices;
    }

    public AudioDevice? GetDefaultCaptureDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                return null;

            // Communications role matches the "default communications device" a dictation app
            // should follow (the headset the user picked for voice), falling back to nothing
            // rather than guessing if none is configured.
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return new AudioDevice { Id = device.ID, Name = device.FriendlyName };
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to resolve default capture device", new() { ["error"] = ex.Message });
            return null;
        }
    }
}
