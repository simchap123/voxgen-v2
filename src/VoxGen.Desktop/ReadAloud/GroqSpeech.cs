using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Transcription;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Groq text-to-speech (Orpheus). WAV only, 200 characters per request — longer text is split by
/// <see cref="ChunkText"/> and synthesized piece by piece.
/// </summary>
public sealed class GroqSpeech : ISpeechSynthesizer
{
    public const string Model = "canopylabs/orpheus-v1-english";
    public const int MaxChars = 200;

    int ISpeechSynthesizer.MaxCharsPerRequest => MaxChars;
    public static readonly string[] Voices = ["hannah", "autumn", "diana", "troy", "austin", "daniel"];
    public const string DefaultVoice = "hannah";

    private readonly HttpClient _http;
    private readonly Func<string?> _getApiKey;

    /// <param name="http">Configured with <see cref="GroqWhisperProvider.BaseUrl"/> as its BaseAddress.</param>
    public GroqSpeech(HttpClient http, Func<string?> getApiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _getApiKey = getApiKey ?? throw new ArgumentNullException(nameof(getApiKey));
    }

    /// <summary>Synthesizes one chunk (≤ 200 chars) to WAV bytes.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        var key = _getApiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new MissingApiKeyException();
        if (text.Length > MaxChars) text = text[..MaxChars];

        using var req = new HttpRequestMessage(HttpMethod.Post, "audio/speech")
        {
            Content = JsonContent.Create(new { model = Model, input = text, voice, response_format = "wav" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await GroqWhisperProvider.SendAsync(_http, req, ct).ConfigureAwait(false);
        await GroqWhisperProvider.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var wav = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return FixStreamingWavHeader(wav);
    }

    /// <summary>
    /// Groq streams its WAV, so the RIFF and data chunk sizes are the 0xFFFFFFFF "unknown" placeholder,
    /// which NAudio's WaveFileReader rejects. Rewrite both to the real lengths. Patches in place and
    /// returns the same array; leaves anything that isn't a RIFF/WAVE file untouched.
    /// </summary>
    public static byte[] FixStreamingWavHeader(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F'
            || wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            return wav;
        }

        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), (uint)(wav.Length - 8));

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BitConverter.ToUInt32(wav, pos + 4);
            var isData = wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a';
            var available = (uint)(wav.Length - (pos + 8));
            if (isData)
            {
                if (size > available) BitConverter.TryWriteBytes(wav.AsSpan(pos + 4, 4), available);
                break;
            }
            if (size > available) break; // malformed non-data chunk — leave as is
            pos += 8 + (int)size + (int)(size & 1); // chunks are word-aligned
        }
        return wav;
    }

    /// <summary>
    /// Splits text into pieces of at most <paramref name="max"/> characters, preferring sentence
    /// ends, then commas/semicolons, then spaces. Stops once <paramref name="totalCap"/> characters
    /// have been emitted so a hovered wall of text can't run up the bill.
    /// </summary>
    public static List<string> ChunkText(string text, int max = MaxChars, int totalCap = 1000)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        var emitted = 0;

        while (remaining.Length > 0 && emitted < totalCap)
        {
            if (remaining.Length <= max)
            {
                chunks.Add(remaining);
                break;
            }

            var window = remaining[..max];
            var cut = LastBreak(window, ". ", "! ", "? ");
            if (cut < max / 3) cut = LastBreak(window, "; ", ", ", ": ");
            if (cut < max / 3) cut = window.LastIndexOf(' ') + 1;
            if (cut <= 0) cut = max;

            var piece = remaining[..cut].Trim();
            if (piece.Length > 0)
            {
                chunks.Add(piece);
                emitted += piece.Length;
            }
            remaining = remaining[cut..].TrimStart();
        }
        return chunks;
    }

    /// <summary>Index just past the last occurrence of any separator's punctuation, or -1.</summary>
    private static int LastBreak(string window, params string[] separators)
    {
        var best = -1;
        foreach (var sep in separators)
        {
            var i = window.LastIndexOf(sep, StringComparison.Ordinal);
            if (i >= 0 && i + 1 > best) best = i + 1;
        }
        return best;
    }
}
