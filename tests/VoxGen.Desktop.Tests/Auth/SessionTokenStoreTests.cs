using System;
using System.IO;
using VoxGen.Desktop.Auth;
using Xunit;

namespace VoxGen.Desktop.Tests.Auth;

public sealed class SessionTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly SessionTokenStore _store;

    public SessionTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "voxgen-session-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "session.bin");
        _store = new SessionTokenStore(_path, new TestLogger());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSession()
    {
        var session = new SupabaseSession
        {
            AccessToken = "header.payload.signature",
            RefreshToken = "refresh-token-xyz",
            ExpiresAtUtc = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc),
            Email = "user@example.com",
        };

        _store.Save(session);

        Assert.True(_store.TryLoad(out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(session.AccessToken, loaded!.AccessToken);
        Assert.Equal(session.RefreshToken, loaded.RefreshToken);
        Assert.Equal(session.ExpiresAtUtc, loaded.ExpiresAtUtc);
        Assert.Equal(session.Email, loaded.Email);
    }

    [Fact]
    public void TryLoad_ReturnsFalse_OnMissingFile()
    {
        Assert.False(_store.TryLoad(out var loaded));
        Assert.Null(loaded);
    }

    [Fact]
    public void TryLoad_ReturnsFalse_OnCorruptedFile()
    {
        File.WriteAllBytes(_path, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        Assert.False(_store.TryLoad(out var loaded));
        Assert.Null(loaded);
    }

    [Fact]
    public void Clear_DeletesTheFile()
    {
        _store.Save(new SupabaseSession
        {
            AccessToken = "a",
            RefreshToken = "r",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        });
        Assert.True(File.Exists(_path));

        _store.Clear();

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Clear_IsIdempotent_WhenFileMissing()
    {
        // Should not throw.
        _store.Clear();
        Assert.False(File.Exists(_path));
    }
}
