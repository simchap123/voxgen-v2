using System.Collections.Generic;
using System.IO;
using VoxGen.Desktop.Settings;
using Xunit;

namespace VoxGen.Desktop.Tests.Settings;

public sealed class SettingsServiceTests
{
    private readonly TestLogger _logger = new();

    [Fact]
    public void TryUpdate_applies_change_and_persists()
    {
        var store = new InMemoryStore();
        var svc = SettingsService.Load(store, _logger);
        var events = new List<SettingsChangedEventArgs>();
        svc.Changed += (_, e) => events.Add(e);

        var ok = svc.TryUpdate(s => s with { HotkeyValue = "LeftShift" }, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("LeftShift", svc.Current.HotkeyValue);
        Assert.Single(events);
        Assert.Equal("LeftShift", events[0].Current.HotkeyValue);
        Assert.Equal("RightAlt", events[0].Previous.HotkeyValue);
        Assert.Equal("LeftShift", store.LastSaved?.HotkeyValue);
    }

    [Fact]
    public void TryUpdate_rolls_back_in_memory_when_persist_fails()
    {
        // PRD §10 rule 6 — write failure must roll the in-memory state back so the UI reverts.
        var store = new InMemoryStore { FailNextSave = true };
        var svc = SettingsService.Load(store, _logger);
        var original = svc.Current;
        var events = new List<SettingsChangedEventArgs>();
        svc.Changed += (_, e) => events.Add(e);

        var ok = svc.TryUpdate(s => s with { HotkeyValue = "LeftShift" }, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(original, svc.Current);
        // Two events fire: the optimistic forward update, then the rollback.
        Assert.Equal(2, events.Count);
        Assert.Equal("LeftShift", events[0].Current.HotkeyValue);
        Assert.Equal(original, events[1].Current);
    }

    [Fact]
    public void TryUpdate_is_noop_when_transform_returns_identical_value()
    {
        var store = new InMemoryStore();
        var svc = SettingsService.Load(store, _logger);
        var events = new List<SettingsChangedEventArgs>();
        svc.Changed += (_, e) => events.Add(e);

        var ok = svc.TryUpdate(s => s, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(events);
        Assert.Null(store.LastSaved);
    }

    [Fact]
    public void Current_reflects_loaded_state_on_construction()
    {
        var preloaded = AppSettings.Defaults with { Language = "fr", Theme = AppTheme.Dark };
        var store = new InMemoryStore { LoadValue = preloaded };
        var svc = SettingsService.Load(store, _logger);

        Assert.Equal(preloaded, svc.Current);
    }

    private sealed class InMemoryStore : ISettingsStore
    {
        public AppSettings LoadValue { get; set; } = AppSettings.Defaults;
        public AppSettings? LastSaved { get; private set; }
        public bool FailNextSave { get; set; }

        public AppSettings Load() => LoadValue;

        public void SaveAndVerify(AppSettings settings)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("simulated persist failure");
            }
            LastSaved = settings;
        }
    }
}
