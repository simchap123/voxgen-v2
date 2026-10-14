using System.Threading;
using System.Threading.Tasks;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>A text-to-speech engine used by read-aloud (NVIDIA Magpie or Groq Orpheus).</summary>
public interface ISpeechSynthesizer
{
    /// <summary>Longest text one request should carry; longer text is chunked by the caller.</summary>
    int MaxCharsPerRequest { get; }

    /// <summary>Synthesizes <paramref name="text"/> to a complete, playable WAV file.</summary>
    Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct);
}
