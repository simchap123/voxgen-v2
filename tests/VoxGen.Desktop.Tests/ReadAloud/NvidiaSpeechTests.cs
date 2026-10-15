using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.ReadAloud;
using VoxGen.Desktop.Transcription;
using Xunit;

namespace VoxGen.Desktop.Tests.ReadAloud;

public class NvidiaSpeechTests
{
    [Fact]
    public void EncodeRequest_ProducesExpectedProtobuf()
    {
        var bytes = NvidiaSpeech.EncodeRequest("Hi", "Magpie-Multilingual.EN-US.Aria");

        // field 1 (text) = "Hi"
        Assert.Equal(new byte[] { 0x0A, 0x02, (byte)'H', (byte)'i' }, bytes[..4]);
        // field 2 (language_code) = "en-US"
        Assert.Equal(new byte[] { 0x12, 0x05 }, bytes[4..6]);
        // field 3 (encoding) = 1, field 4 (sample rate) = 22050 as varint (0xA2 0xAC 0x01)
        Assert.Equal(new byte[] { 0x18, 0x01, 0x20, 0xA2, 0xAC, 0x01 }, bytes[11..17]);
        // field 5 (voice) last
        Assert.Equal(0x2A, bytes[17]);
    }

    [Fact]
    public void Frame_Unframe_RoundTrip()
    {
        var msg = new byte[300];
        new Random(1).NextBytes(msg);
        var framed = NvidiaSpeech.Frame(msg);
        Assert.Equal(0, framed[0]);
        Assert.Equal(msg, NvidiaSpeech.Unframe(framed));
    }

    [Fact]
    public void DecodeAudio_SkipsOtherFields_ReturnsField1()
    {
        // field 2 varint first, then field 1 bytes [1,2,3]
        var msg = new byte[] { 0x10, 0x05, 0x0A, 0x03, 1, 2, 3 };
        Assert.Equal(new byte[] { 1, 2, 3 }, NvidiaSpeech.DecodeAudio(msg));
    }

    [Theory]
    [InlineData("Leo", "Magpie-Multilingual.EN-US.Leo")]
    [InlineData("Aria Calm", "Magpie-Multilingual.EN-US.Aria.Calm")]
    [InlineData("nobody", "Magpie-Multilingual.EN-US.Aria")]
    public void FullVoiceName_MapsKnownVoices_FallsBackToDefault(string voice, string expected) =>
        Assert.Equal(expected, NvidiaSpeech.FullVoiceName(voice));

    private sealed class FakeGrpc : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? Request;
        public FakeGrpc(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(_respond(request));
        }
    }

    [Fact]
    public async Task Synthesize_WrapsPcmInWav_AndSendsGrpcHeaders()
    {
        var pcm = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var reply = NvidiaSpeech.Frame(new byte[] { 0x0A, (byte)pcm.Length }.AsSpan().ToArray().Concat(pcm));
        var handler = new FakeGrpc(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(reply) });
        var speech = new NvidiaSpeech(new HttpClient(handler), () => "nvapi-test");

        var wav = await speech.SynthesizeAsync("Hello", "Ray", CancellationToken.None);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(HttpVersion.Version20, handler.Request!.Version);
        Assert.True(handler.Request.Headers.Contains("function-id"));
        Assert.Equal("application/grpc", handler.Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Synthesize_GrpcUnauthenticated_ThrowsInvalidKey()
    {
        var handler = new FakeGrpc(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            r.TrailingHeaders.Add("grpc-status", "16");
            return r;
        });
        var speech = new NvidiaSpeech(new HttpClient(handler), () => "nvapi-bad");
        await Assert.ThrowsAsync<InvalidApiKeyException>(() => speech.SynthesizeAsync("x", "Aria", CancellationToken.None));
    }

    [Fact]
    public async Task Synthesize_TransientSessionError_RetriesThenSucceeds()
    {
        NvidiaSpeech.RetryDelayMs = 1;
        var pcm = new byte[] { 0, 1, 2, 3 };
        var calls = 0;
        var handler = new FakeGrpc(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var bad = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                bad.TrailingHeaders.Add("grpc-status", "5");
                bad.TrailingHeaders.Add("grpc-message", "no existing session found");
                return bad;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(NvidiaSpeech.Frame(new byte[] { 0x0A, (byte)pcm.Length }.Concat(pcm))),
            };
        });

        var wav = await new NvidiaSpeech(new HttpClient(handler), () => "nvapi-test").SynthesizeAsync("Hi", "Aria", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(44 + pcm.Length, wav.Length);
    }

    [Fact]
    public async Task Synthesize_NoKey_Throws() =>
        await Assert.ThrowsAsync<MissingApiKeyException>(() =>
            new NvidiaSpeech(new HttpClient(), () => null).SynthesizeAsync("x", "Aria", CancellationToken.None));
}

internal static class ByteArrayExtensions
{
    public static byte[] Concat(this byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
