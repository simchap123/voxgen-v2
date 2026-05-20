using System;
using System.Buffers.Binary;

namespace VoxGen.Desktop.Audio;

/// <summary>
/// Hand-rolls a canonical 44-byte PCM WAV header around a raw PCM payload (PRD §8.4 — the
/// backend receives WAV, so we avoid an audio-encoder dependency; the WAV header is trivial).
///
/// This type is intentionally pure and free of any NAudio dependency so the header layout is
/// unit-testable in isolation (see WavWriterTests). All multi-byte fields are little-endian,
/// per the RIFF/WAVE spec.
/// </summary>
public static class WavWriter
{
    /// <summary>The fixed size of a canonical PCM WAV header (RIFF + fmt + data chunk headers).</summary>
    public const int HeaderSize = 44;

    /// <summary>
    /// Wraps <paramref name="pcm"/> in a complete RIFF/WAVE container and returns the full file bytes.
    /// </summary>
    /// <param name="pcm">Raw little-endian PCM samples (no header). May be empty.</param>
    /// <param name="sampleRate">Samples per second per channel, e.g. 16000.</param>
    /// <param name="channels">Channel count, e.g. 1 for mono.</param>
    /// <param name="bitsPerSample">Bit depth, e.g. 16.</param>
    /// <returns>A byte[] of length <see cref="HeaderSize"/> + <paramref name="pcm"/>.Length.</returns>
    public static byte[] Write(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Must be a positive multiple of 8.");

        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataSize = pcm.Length;
        // RIFF chunk size = everything after the first 8 bytes ("RIFF" + this size field):
        // 4 ("WAVE") + (8 + 16) fmt chunk + (8 + dataSize) data chunk = 36 + dataSize.
        int riffChunkSize = 36 + dataSize;

        var buffer = new byte[HeaderSize + dataSize];
        var span = buffer.AsSpan();

        // ---- RIFF descriptor ----
        WriteAscii(span, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), riffChunkSize);
        WriteAscii(span, 8, "WAVE");

        // ---- "fmt " subchunk (16 bytes for PCM) ----
        WriteAscii(span, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);          // Subchunk1Size for PCM
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);           // AudioFormat = 1 (PCM)
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), (short)bitsPerSample);

        // ---- "data" subchunk ----
        WriteAscii(span, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataSize);

        pcm.AsSpan().CopyTo(span.Slice(HeaderSize));

        return buffer;
    }

    private static void WriteAscii(Span<byte> dest, int offset, string fourCc)
    {
        // All chunk tags are 4-char ASCII; write the bytes directly so the layout is unambiguous.
        for (int i = 0; i < fourCc.Length; i++)
            dest[offset + i] = (byte)fourCc[i];
    }
}
