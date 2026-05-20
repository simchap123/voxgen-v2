using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Backend;
using Xunit;

namespace VoxGen.Desktop.Tests.Backend;

public sealed class VoxGenBackendExceptionMappingTests
{
    private static readonly byte[] DummyWav = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF" — content irrelevant for these tests.

    [Fact]
    public async Task Transcribe_Returns401_ThrowsUnauthenticated()
    {
        await using var fixture = MakeClient(HttpStatusCode.Unauthorized, body: "{\"error\":\"jwt expired\"}");
        await Assert.ThrowsAsync<UnauthenticatedException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Returns402_ThrowsTrialExpired()
    {
        await using var fixture = MakeClient((HttpStatusCode)402, body: "{\"message\":\"trial over\"}");
        await Assert.ThrowsAsync<TrialExpiredException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Returns403_ThrowsQuotaExceeded()
    {
        await using var fixture = MakeClient(HttpStatusCode.Forbidden, body: "{\"message\":\"monthly cap\"}");
        await Assert.ThrowsAsync<QuotaExceededException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Returns429_ThrowsRateLimited_WithRetryAfterDelta()
    {
        await using var fixture = MakeClient(
            HttpStatusCode.TooManyRequests,
            body: "{\"message\":\"slow down\"}",
            configureResponse: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task Transcribe_Returns500_ThrowsBackendUnavailable()
    {
        await using var fixture = MakeClient(HttpStatusCode.InternalServerError, body: "{\"error\":\"boom\"}");
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Returns503_ThrowsBackendUnavailable()
    {
        await using var fixture = MakeClient(HttpStatusCode.ServiceUnavailable, body: "");
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            fixture.Client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_NetworkFailure_ThrowsBackendUnavailable()
    {
        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("dns went away"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var client = new VoxGenBackendClient(http);

        var ex = await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            client.TranscribeAsync(DummyWav, "token", TranscriptionOptions.Defaults, CancellationToken.None));
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ValidateLicense_Returns401_ThrowsUnauthenticated()
    {
        await using var fixture = MakeClient(HttpStatusCode.Unauthorized, body: "{}");
        await Assert.ThrowsAsync<UnauthenticatedException>(() =>
            fixture.Client.ValidateLicenseAsync("token", CancellationToken.None));
    }

    [Fact]
    public async Task ValidateLicense_Returns500_ThrowsBackendUnavailable()
    {
        await using var fixture = MakeClient(HttpStatusCode.BadGateway, body: "{}");
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            fixture.Client.ValidateLicenseAsync("token", CancellationToken.None));
    }

    // --- helpers ---------------------------------------------------------------------

    private static Fixture MakeClient(
        HttpStatusCode status,
        string body,
        Action<HttpResponseMessage>? configureResponse = null)
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            configureResponse?.Invoke(resp);
            return resp;
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://backend.example.com/") };
        return new Fixture(new VoxGenBackendClient(http), http);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public VoxGenBackendClient Client { get; }
        private readonly HttpClient _http;

        public Fixture(VoxGenBackendClient client, HttpClient http)
        {
            Client = client;
            _http = http;
        }

        public ValueTask DisposeAsync()
        {
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Tiny test double — invokes the supplied lambda per request.</summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(_responder(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
