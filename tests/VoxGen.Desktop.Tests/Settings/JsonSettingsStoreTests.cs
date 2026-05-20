using System;
using System.IO;
using VoxGen.Desktop.Settings;
using Xunit;

namespace VoxGen.Desktop.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;
    private readonly TestLogger _logger = new();

    public JsonSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "voxgen-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Load_returns_defaults_when_file_does_not_exist()
    {
        var store = new JsonSettingsStore(_path, _logger);
        Assert.Equal(AppSettings.Defaults, store.Load());
    }

    [Fact]
    public void SaveAndVerify_persists_and_round_trips_every_field()
    {
        var store = new JsonSettingsStore(_path, _logger);
        var settings = AppSettings.Defaults with
        {
            SelectedMicrophoneId = "{0.0.1.00000000}.{abc-123}",
            SelectedMicrophoneName = "External Mic",
            HotkeyMode = HotkeyMode.Toggle,
            HotkeyValue = "RightControl",
            CleanupEnabled = false,
            SaveTextHistoryLocal = false,
            SaveAudioHistoryLocal = true,
            UseLocalHistoryForAi = true,
            StartupOnBoot = true,
            OverlayEnabled = false,
            Language = "es",
            Theme = AppTheme.Dark,
        };

        store.SaveAndVerify(settings);
        Assert.True(File.Exists(_path));

        var reloaded = new JsonSettingsStore(_path, _logger).Load();
        Assert.Equal(settings, reloaded);
    }

    [Fact]
    public void Load_throws_when_file_is_corrupted()
    {
        // PRD §10 — never silently fall back to defaults when a real file exists but is broken,
        // because the next SaveAndVerify would then overwrite the user's good data with defaults.
        File.WriteAllText(_path, "{ not valid json");
        var store = new JsonSettingsStore(_path, _logger);
        Assert.ThrowsAny<Exception>(() => store.Load());
    }

    [Fact]
    public void SaveAndVerify_does_not_leave_temp_file_behind()
    {
        var store = new JsonSettingsStore(_path, _logger);
        store.SaveAndVerify(AppSettings.Defaults with { HotkeyValue = "LeftAlt" });
        Assert.False(File.Exists(_path + ".tmp"));
    }
}
