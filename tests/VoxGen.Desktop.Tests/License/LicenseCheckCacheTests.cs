using System;
using System.IO;
using VoxGen.Desktop.License;
using Xunit;

namespace VoxGen.Desktop.Tests.License;

public sealed class LicenseCheckCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly LicenseCheckCache _cache;

    public LicenseCheckCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "voxgen-license-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "license.json");
        _cache = new LicenseCheckCache(_path, new TestLogger());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void IsWithinOfflineGrace_ReturnsFalse_WhenNoCacheExists()
    {
        Assert.False(_cache.IsWithinOfflineGrace(TimeSpan.FromDays(10)));
    }

    [Fact]
    public void IsWithinOfflineGrace_ReturnsTrue_WhenValidatedInsideWindow()
    {
        var now = DateTime.UtcNow;
        _cache.Save(MakeStatus(validatedAtUtc: now.AddDays(-3)));

        Assert.True(_cache.IsWithinOfflineGrace(TimeSpan.FromDays(10)));
    }

    [Fact]
    public void IsWithinOfflineGrace_ReturnsFalse_WhenCacheOlderThanWindow()
    {
        var now = DateTime.UtcNow;
        _cache.Save(MakeStatus(validatedAtUtc: now.AddDays(-30)));

        Assert.False(_cache.IsWithinOfflineGrace(TimeSpan.FromDays(10)));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var status = new LicenseStatus
        {
            State = LicenseState.Trial,
            TrialDaysLeft = 7,
            PlanName = "Trial",
            ValidatedAtUtc = new DateTime(2026, 4, 15, 12, 34, 56, DateTimeKind.Utc),
            NextValidationAtUtc = new DateTime(2026, 4, 16, 12, 34, 56, DateTimeKind.Utc),
        };

        _cache.Save(status);
        var loaded = _cache.Load();

        Assert.NotNull(loaded);
        Assert.Equal(status.State, loaded!.State);
        Assert.Equal(status.TrialDaysLeft, loaded.TrialDaysLeft);
        Assert.Equal(status.PlanName, loaded.PlanName);
        Assert.Equal(status.ValidatedAtUtc, loaded.ValidatedAtUtc);
        Assert.Equal(status.NextValidationAtUtc, loaded.NextValidationAtUtc);
    }

    [Fact]
    public void Load_ReturnsNull_OnMissingFile()
    {
        Assert.Null(_cache.Load());
    }

    [Fact]
    public void Load_ReturnsNull_OnCorruptedFile()
    {
        File.WriteAllText(_path, "this is not valid json {{{");
        Assert.Null(_cache.Load());
    }

    private static LicenseStatus MakeStatus(DateTime validatedAtUtc) => new()
    {
        State = LicenseState.Active,
        TrialDaysLeft = 0,
        PlanName = "Pro Monthly",
        ValidatedAtUtc = validatedAtUtc,
        NextValidationAtUtc = validatedAtUtc.AddHours(24),
    };
}
