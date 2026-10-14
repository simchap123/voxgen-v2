using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.ReadAloud;
using VoxGen.Desktop.Transcription;
using Xunit;

namespace VoxGen.Desktop.Tests.ReadAloud;

public class GroqSpeechTests
{
    [Fact]
    public void ChunkText_ShortText_IsOneChunk()
    {
        Assert.Equal(new[] { "Save changes" }, GroqSpeech.ChunkText("Save changes"));
    }

    [Fact]
    public void ChunkText_LongText_SplitsAtSentences_AllUnderLimit()
    {
        var sentence = "This is a sentence that is about sixty characters long ok. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 8));

        var chunks = GroqSpeech.ChunkText(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= GroqSpeech.MaxCharsPerRequest, $"chunk too long: {c.Length}"));
        Assert.All(chunks, c => Assert.EndsWith(".", c));
    }

    [Fact]
    public void ChunkText_NoSpaces_HardSplits()
    {
        var chunks = GroqSpeech.ChunkText(new string('a', 450));
        Assert.Equal(new[] { 200, 200, 50 }, chunks.Select(c => c.Length));
    }

    [Fact]
    public void ChunkText_StopsAtTotalCap()
    {
        var text = string.Concat(Enumerable.Repeat("Word word word word word. ", 200));
        var chunks = GroqSpeech.ChunkText(text, totalCap: 1000);
        Assert.InRange(chunks.Sum(c => c.Length), 1, 1200);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 82, 73, 70, 70 }) };
        }
    }

    [Fact]
    public async Task Synthesize_PostsOrpheusRequest_ReturnsWav()
    {
        var handler = new Capture();
        var http = new HttpClient(handler) { BaseAddress = new Uri(GroqWhisperProvider.BaseUrl) };
        var speech = new GroqSpeech(http, () => "gsk_test");

        var wav = await speech.SynthesizeAsync("Hello there", "troy", CancellationToken.None);

        Assert.Equal(4, wav.Length);
        Assert.EndsWith("audio/speech", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Contains("\"model\":\"canopylabs/orpheus-v1-english\"", handler.Body);
        Assert.Contains("\"voice\":\"troy\"", handler.Body);
        Assert.Contains("\"response_format\":\"wav\"", handler.Body);
    }

    [Fact]
    public async Task Synthesize_NoKey_Throws()
    {
        var speech = new GroqSpeech(new HttpClient(new Capture()) { BaseAddress = new Uri(GroqWhisperProvider.BaseUrl) }, () => null);
        await Assert.ThrowsAsync<MissingApiKeyException>(() => speech.SynthesizeAsync("x", "troy", CancellationToken.None));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 0.5)]
    [InlineData(9.0, 2.5)]
    [InlineData(1.6, 1.5)]
    [InlineData(1.4, 1.5)]
    public void SpeechPlayer_ClampRate_SnapsToQuarterStepsWithinRange(double input, double expected)
    {
        Assert.Equal(expected, SpeechPlayer.ClampRate(input));
    }

    [Theory]
    [InlineData("   ", null)]
    [InlineData("—", null)]
    [InlineData("  Save\n\n  changes ", "Save changes")]
    public void Clean_NormalizesText(string input, string? expected)
    {
        Assert.Equal(expected, ScreenTextReader.Clean(input));
    }
}
