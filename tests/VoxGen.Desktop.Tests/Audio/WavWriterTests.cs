using System;
using System.Buffers.Binary;
using System.Text;
using VoxGen.Desktop.Audio;
using Xunit;

namespace VoxGen.Desktop.Tests.Audio;

/// <summary>
/// Verifies the hand-rolled 44-byte PCM WAV header (PRD §8.4). The backend parses these bytes,
/// so every field must be exactly right and little-endian.
/// </summary>
public sealed class WavWriterTests
{
    private static string Ascii(byte[] wav, int offset, int length) =>
        Encoding.ASCII.GetString(wav, offset, length);

    private static int I32(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset, 4));

    private static short I16(byte[] wav, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(offset, 2));

    [Fact]
    public void Write_mono_16k_16bit_emits_correct_header()
    {
        const int sampleRate = 16000;
        const int channels = 1;
        const int bits = 16;
        var pcm = new byte[3200]; // 0.1s of 16k/mono/16-bit silence

        byte[] wav = WavWriter.Write(pcm, sampleRate, channels, bits);

        // Total length and the RIFF/WAVE tags.
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, I32(wav, 4)); // RIFF chunk size
        Assert.Equal("WAVE", Ascii(wav, 8, 4));

        // "fmt " subchunk.
        Assert.Equal("fmt ", Ascii(wav, 12, 4));
        Assert.Equal(16, I32(wav, 16));                       // Subchunk1Size (PCM)
        Assert.Equal(1, I16(wav, 20));                        // AudioFormat = PCM
        Assert.Equal((short)channels, I16(wav, 22));
        Assert.Equal(sampleRate, I32(wav, 24));
        Assert.Equal(sampleRate * channels * (bits / 8), I32(wav, 28));   // byte rate
        Assert.Equal((short)(channels * (bits / 8)), I16(wav, 32));       // block align
        Assert.Equal((short)bits, I16(wav, 34));

        // "data" subchunk.
        Assert.Equal("data", Ascii(wav, 36, 4));
        Assert.Equal(pcm.Length, I32(wav, 40));
    }

    [Fact]
    public void Write_stereo_44100_16bit_emits_correct_header()
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const int bits = 16;
        var pcm = new byte[8820]; // arbitrary stereo payload

        byte[] wav = WavWriter.Write(pcm, sampleRate, channels, bits);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, I32(wav, 4));
        Assert.Equal("WAVE", Ascii(wav, 8, 4));

        Assert.Equal("fmt ", Ascii(wav, 12, 4));
        Assert.Equal(16, I32(wav, 16));
        Assert.Equal(1, I16(wav, 20));
        Assert.Equal((short)channels, I16(wav, 22));
        Assert.Equal(sampleRate, I32(wav, 24));
        Assert.Equal(sampleRate * channels * (bits / 8), I32(wav, 28)); // 44100 * 2 * 2 = 176400
        Assert.Equal((short)(channels * (bits / 8)), I16(wav, 32));      // 4
        Assert.Equal((short)bits, I16(wav, 34));

        Assert.Equal("data", Ascii(wav, 36, 4));
        Assert.Equal(pcm.Length, I32(wav, 40));
    }

    [Fact]
    public void Write_copies_pcm_payload_after_header()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 250, 251, 252, 253 };

        byte[] wav = WavWriter.Write(pcm, 16000, 1, 16);

        Assert.Equal(WavWriter.HeaderSize + pcm.Length, wav.Length);
        for (int i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i], wav[WavWriter.HeaderSize + i]);
    }

    [Fact]
    public void Write_empty_pcm_still_emits_valid_44_byte_header()
    {
        byte[] wav = WavWriter.Write(Array.Empty<byte>(), 16000, 1, 16);

        Assert.Equal(44, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0, 4));
        Assert.Equal(36, I32(wav, 4));      // 36 + 0
        Assert.Equal("data", Ascii(wav, 36, 4));
        Assert.Equal(0, I32(wav, 40));      // data size == 0
    }

    [Fact]
    public void Write_rejects_null_pcm()
    {
        Assert.Throws<ArgumentNullException>(() => WavWriter.Write(null!, 16000, 1, 16));
    }

    [Theory]
    [InlineData(0, 1, 16)]
    [InlineData(16000, 0, 16)]
    [InlineData(16000, 1, 0)]
    [InlineData(16000, 1, 12)] // not a multiple of 8
    public void Write_rejects_invalid_format(int sampleRate, int channels, int bits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WavWriter.Write(Array.Empty<byte>(), sampleRate, channels, bits));
    }
}
