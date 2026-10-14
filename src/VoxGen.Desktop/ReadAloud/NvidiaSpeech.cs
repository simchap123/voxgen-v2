using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Transcription;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// NVIDIA Magpie TTS (free endpoint on build.nvidia.com) via the hosted Riva gRPC service.
///
/// gRPC is spoken directly over HTTP/2 with HttpClient — a 5-byte frame header plus a hand-encoded
/// protobuf message — so no gRPC/protobuf packages are needed. Only two messages are used:
///   SynthesizeSpeechRequest { 1:text, 2:language_code, 3:encoding(LINEAR_PCM=1), 4:sample_rate_hz, 5:voice_name }
///   SynthesizeSpeechResponse { 1:audio (raw 16-bit mono PCM) }
/// </summary>
public sealed class NvidiaSpeech : ISpeechSynthesizer
{
    public const string Endpoint = "https://grpc.nvcf.nvidia.com/nvidia.riva.tts.RivaSpeechSynthesis/Synthesize";
    public const string MagpieFunctionId = "877104f7-e885-42b9-8de8-f6e4c6303969";
    public const int SampleRate = 22050;

    public static readonly string[] Voices = ["Aria", "Mia", "Sofia", "Jason", "Leo", "Ray"];
    public const string DefaultVoice = "Aria";

    private readonly HttpClient _http;
    private readonly Func<string?> _getApiKey;

    /// <param name="http">Any HttpClient; requests are sent as HTTP/2 (required by gRPC).</param>
    public NvidiaSpeech(HttpClient http, Func<string?> getApiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _getApiKey = getApiKey ?? throw new ArgumentNullException(nameof(getApiKey));
    }

    /// <summary>Magpie handles 600+ chars per call; ~400 keeps the first audio quick.</summary>
    public int MaxCharsPerRequest => 400;

    public static string FullVoiceName(string voice) =>
        $"Magpie-Multilingual.EN-US.{(Array.IndexOf(Voices, voice) >= 0 ? voice : DefaultVoice)}";

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        var key = _getApiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new MissingApiKeyException();

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(Frame(EncodeRequest(text, FullVoiceName(voice)))),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        req.Headers.TryAddWithoutValidation("te", "trailers");
        req.Headers.TryAddWithoutValidation("authorization", "Bearer " + key);
        req.Headers.TryAddWithoutValidation("function-id", MagpieFunctionId);

        using var response = await GroqWhisperProvider.SendAsync(_http, req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var status = Header(response, "grpc-status");
        var message = Uri.UnescapeDataString(Header(response, "grpc-message"));
        if ((int)response.StatusCode is 401 or 403 || status is "16" or "7")
            throw new InvalidApiKeyException(string.IsNullOrEmpty(message) ? "NVIDIA rejected the API key." : message);
        if ((int)response.StatusCode == 429 || status == "8")
            throw new GroqRateLimitedException("NVIDIA rate limit reached — wait a moment.");
        if (!response.IsSuccessStatusCode || status is "14" or "13")
            throw new GroqUnavailableException($"NVIDIA voice service unavailable ({(int)response.StatusCode} {status} {message}).".Trim());
        if (!string.IsNullOrEmpty(status) && status != "0")
            throw new GroqException($"NVIDIA voice error {status}: {message}");

        var pcm = DecodeAudio(Unframe(body));
        if (pcm.Length == 0) throw new GroqException("NVIDIA returned no audio.");
        return WavWriter.Write(pcm, SampleRate, 1, 16);
    }

    // ---------- protobuf / gRPC framing (pure; unit-tested) ----------

    public static byte[] EncodeRequest(string text, string voiceName)
    {
        var b = new List<byte>();
        WriteString(b, 1, text);
        WriteString(b, 2, "en-US");
        WriteVarintField(b, 3, 1);           // LINEAR_PCM
        WriteVarintField(b, 4, SampleRate);
        WriteString(b, 5, voiceName);
        return b.ToArray();
    }

    /// <summary>gRPC length-prefixed message: 1 byte compressed flag (0) + 4 byte big-endian length.</summary>
    public static byte[] Frame(byte[] message)
    {
        var frame = new byte[5 + message.Length];
        frame[1] = (byte)(message.Length >> 24);
        frame[2] = (byte)(message.Length >> 16);
        frame[3] = (byte)(message.Length >> 8);
        frame[4] = (byte)message.Length;
        message.CopyTo(frame, 5);
        return frame;
    }

    public static byte[] Unframe(byte[] body)
    {
        if (body.Length < 5) return Array.Empty<byte>();
        var len = (body[1] << 24) | (body[2] << 16) | (body[3] << 8) | body[4];
        return len <= body.Length - 5 ? body.AsSpan(5, len).ToArray() : Array.Empty<byte>();
    }

    /// <summary>Extracts field 1 (audio bytes) from a SynthesizeSpeechResponse; skips other fields.</summary>
    public static byte[] DecodeAudio(byte[] message)
    {
        var i = 0;
        while (i < message.Length)
        {
            var tag = ReadVarint(message, ref i);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0: ReadVarint(message, ref i); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    var len = (int)ReadVarint(message, ref i);
                    if (field == 1) return message.AsSpan(i, len).ToArray();
                    i += len;
                    break;
                default: return Array.Empty<byte>();
            }
        }
        return Array.Empty<byte>();
    }

    private static void WriteVarint(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }

    private static void WriteVarintField(List<byte> b, int field, long v)
    {
        WriteVarint(b, (ulong)(field << 3));
        WriteVarint(b, (ulong)v);
    }

    private static void WriteString(List<byte> b, int field, string s)
    {
        var data = Encoding.UTF8.GetBytes(s);
        WriteVarint(b, (ulong)(field << 3 | 2));
        WriteVarint(b, (ulong)data.Length);
        b.AddRange(data);
    }

    private static ulong ReadVarint(byte[] buf, ref int i)
    {
        ulong v = 0;
        var shift = 0;
        while (i < buf.Length)
        {
            var x = buf[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.TrailingHeaders.TryGetValues(name, out var t) ? string.Join(",", t)
        : r.Headers.TryGetValues(name, out var h) ? string.Join(",", h)
        : "";
}
