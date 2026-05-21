using System;
using System.IO;
using NAudio.Wave;

namespace VoxGen.Desktop.Audio;

/// <summary>
/// Short, synthesized UI feedback sounds. No audio assets are shipped — tones are generated in
/// memory and played through NAudio (already a dependency). All playback is best-effort and never
/// throws: a missing or busy output device must not affect dictation (hard invariant — focus/flow
/// is sacred).
/// </summary>
public static class SoundCue
{
    private const int SampleRate = 44100;

    /// <summary>
    /// A soft two-note rising chime, played the moment transcription begins — i.e. right after the
    /// user releases the key and VoxGen is fetching the result from the cloud. Subtle by design.
    /// </summary>
    public static void PlayTranscribing()
    {
        try
        {
            var pcm = BuildRisingChime();
            var stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(SampleRate, 16, 1));
            var output = new WaveOutEvent { Volume = 0.45f };
            output.PlaybackStopped += (_, _) =>
            {
                try { output.Dispose(); stream.Dispose(); } catch { /* ignore */ }
            };
            output.Init(stream);
            output.Play();
        }
        catch
        {
            // Sound is a nicety; never let it interfere with dictation.
        }
    }

    private static byte[] BuildRisingChime()
    {
        // Two short notes (E5 -> B5, a bright rising fifth), gentle attack/release, modest level.
        double[] freqs = { 659.25, 987.77 };
        const double noteSeconds = 0.12;
        const double amplitude = 0.28; // fraction of full scale

        int samplesPerNote = (int)(SampleRate * noteSeconds);
        int total = samplesPerNote * freqs.Length;
        var bytes = new byte[total * 2]; // 16-bit mono

        int idx = 0;
        foreach (var f in freqs)
        {
            for (int i = 0; i < samplesPerNote; i++)
            {
                double t = i / (double)SampleRate;
                double env = Envelope(i, samplesPerNote);
                double s = Math.Sin(2 * Math.PI * f * t) * amplitude * env;
                short val = (short)(s * short.MaxValue);
                bytes[idx++] = (byte)(val & 0xFF);
                bytes[idx++] = (byte)((val >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    /// <summary>Linear attack (first 15%) + release (last 45%) so the tone fades in/out without clicks.</summary>
    private static double Envelope(int i, int n)
    {
        double attack = n * 0.15;
        double release = n * 0.45;
        if (i < attack) return i / attack;
        if (i > n - release) return (n - i) / release;
        return 1.0;
    }
}
