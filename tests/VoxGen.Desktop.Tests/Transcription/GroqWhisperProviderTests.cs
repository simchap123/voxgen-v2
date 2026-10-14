using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Transcription;
using Xunit;

namespace VoxGen.Desktop.Tests.Transcription;

public class GroqWhisperProviderTests
{
    private static readonly AudioClip Clip = new()
    {
        WavBytes = new byte[] { 1, 2, 3, 4 },
        SampleRate = 16000,
        Channels = 1,
        Duration = TimeSpan.FromSeconds(1),
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (GroqWhisperProvider provider, FakeHandler handler) Make(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string? key = "gsk_test", bool cleanup = false)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(GroqWhisperProvider.BaseUrl) };
        var provider = new GroqWhisperProvider(http, () => key, () => cleanup, () => null, new TestLogger());
        return (provider, handler);
    }

    [Fact]
    public async Task NoKey_ThrowsMissingApiKey_WithoutNetworkCall()
    {
        var (provider, handler) = Make(_ => throw new InvalidOperationException("should not call"), key: null);
        await Assert.ThrowsAsync<MissingApiKeyException>(() => provider.TranscribeAsync(Clip, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Whisper_Success_ReturnsTrimmedText_AndSendsBearerKey()
    {
        var (provider, handler) = Make(_ => Json(HttpStatusCode.OK, "{\"text\":\"  hello world \"}"));
        var result = await provider.TranscribeAsync(Clip, CancellationToken.None);

        Assert.Equal("hello world", result.FinalText);
        Assert.False(result.CleanupApplied);
        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("audio/transcriptions", req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer gsk_test", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Cleanup_Enabled_UsesChatResult()
    {
        var (provider, handler) = Make(r => r.RequestUri!.AbsoluteUri.EndsWith("chat/completions")
            ? Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello, world.\"}}]}")
            : Json(HttpStatusCode.OK, "{\"text\":\"um hello world\"}"), cleanup: true);

        var result = await provider.TranscribeAsync(Clip, CancellationToken.None);

        Assert.Equal("Hello, world.", result.FinalText);
        Assert.Equal("um hello world", result.RawText);
        Assert.True(result.CleanupApplied);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cleanup_Failure_FallsBackToRawTranscript()
    {
        var (provider, _) = Make(r => r.RequestUri!.AbsoluteUri.EndsWith("chat/completions")
            ? Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}")
            : Json(HttpStatusCode.OK, "{\"text\":\"raw words\"}"), cleanup: true);

        var result = await provider.TranscribeAsync(Clip, CancellationToken.None);

        Assert.Equal("raw words", result.FinalText);
        Assert.False(result.CleanupApplied);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(InvalidApiKeyException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(GroqRateLimitedException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(GroqUnavailableException))]
    public async Task Whisper_ErrorStatus_MapsToTypedException(HttpStatusCode code, Type expected)
    {
        var (provider, _) = Make(_ => Json(code, "{\"error\":{\"message\":\"nope\"}}"));
        var ex = await Assert.ThrowsAnyAsync<GroqException>(() => provider.TranscribeAsync(Clip, CancellationToken.None));
        Assert.IsType(expected, ex);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsUnavailable()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException("dns"));
        await Assert.ThrowsAsync<GroqUnavailableException>(() => provider.TranscribeAsync(Clip, CancellationToken.None));
    }
}
