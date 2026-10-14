using System;
using System.IO;
using VoxGen.Desktop.History;
using VoxGen.Desktop.Settings;
using VoxGen.Desktop.Updater;
using Xunit;

namespace VoxGen.Desktop.Tests.History;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voxgen-history-" + Guid.NewGuid().ToString("N"));

    private HistoryStore Make() =>
        new(Path.Combine(_dir, "history.jsonl"), Path.Combine(_dir, "recordings"), new TestLogger());

    [Fact]
    public void Add_TextAndAudio_RoundTripsAndWritesWav()
    {
        var store = Make();
        var entry = store.Add("Hello.", "hello", TimeSpan.FromSeconds(2), new byte[] { 1, 2, 3 }, saveText: true);

        Assert.NotNull(entry);
        var loaded = Assert.Single(Make().LoadRecent());
        Assert.Equal("Hello.", loaded.Text);
        Assert.Equal(2000, loaded.DurationMs);
        Assert.True(File.Exists(store.AudioPath(loaded)));
    }

    [Fact]
    public void Add_AudioOnly_KeepsNoText()
    {
        var store = Make();
        store.Add("secret", "secret", TimeSpan.FromSeconds(1), new byte[] { 9 }, saveText: false);
        var loaded = Assert.Single(store.LoadRecent());
        Assert.Equal(string.Empty, loaded.Text);
        Assert.NotNull(loaded.AudioFile);
    }

    [Fact]
    public void Add_NothingEnabled_SavesNothing()
    {
        var store = Make();
        Assert.Null(store.Add("x", null, TimeSpan.Zero, null, saveText: false));
        Assert.Empty(store.LoadRecent());
    }

    [Fact]
    public void LoadRecent_SkipsTornLines_AndOrdersNewestFirst()
    {
        var store = Make();
        store.Add("first", null, TimeSpan.Zero, null, saveText: true);
        File.AppendAllText(Path.Combine(_dir, "history.jsonl"), "{not json\n");
        System.Threading.Thread.Sleep(5);
        store.Add("second", null, TimeSpan.Zero, null, saveText: true);

        var loaded = store.LoadRecent();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("second", loaded[0].Text);
    }

    [Fact]
    public void Clear_RemovesEntriesAndRecordings()
    {
        var store = Make();
        store.Add("a", null, TimeSpan.Zero, new byte[] { 1 }, saveText: true);
        store.Clear();
        Assert.Empty(store.LoadRecent());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "recordings")));
    }

    [Fact]
    public void ApiKeyStore_SavesEncrypted_AndReloads()
    {
        var path = Path.Combine(_dir, "groq-key.bin");
        new ApiKeyStore(path, new TestLogger()).Save("gsk_abcdef123456");

        Assert.DoesNotContain("gsk_abcdef123456", File.ReadAllText(path));
        var reloaded = new ApiKeyStore(path, new TestLogger());
        Assert.Equal("gsk_abcdef123456", reloaded.Get());

        reloaded.Clear();
        Assert.False(new ApiKeyStore(path, new TestLogger()).HasKey);
    }

    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("v2.0.11-preview", "2.0.11")]
    [InlineData("2.3.4", "2.3.4")]
    public void UpdateService_ParsesReleaseTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Fact]
    public void UpdateService_RejectsGarbageTag() => Assert.Null(UpdateService.ParseVersion("latest"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
