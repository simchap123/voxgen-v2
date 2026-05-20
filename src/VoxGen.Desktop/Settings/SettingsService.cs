using System;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Settings;

public sealed class SettingsChangedEventArgs : EventArgs
{
    public AppSettings Previous { get; }
    public AppSettings Current { get; }

    public SettingsChangedEventArgs(AppSettings previous, AppSettings current)
    {
        Previous = previous;
        Current = current;
    }
}

/// <summary>
/// The single source of truth for user settings — PRD §10 rule 1.
///
/// Every change goes through <see cref="TryUpdate"/>, which executes the §10 rule 5 sequence:
///   (1) compute next from current via the caller's transform,
///   (2) update in-memory state and notify subscribers (optimistic UI),
///   (3) persist + verify via the store,
///   (4) on failure, roll back in-memory state and notify again so the UI reverts (rule 6).
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private AppSettings _current;

    /// <summary>
    /// Fired whenever <see cref="Current"/> changes — including the rollback event when a write fails.
    /// Subscribers should bind to whatever <see cref="SettingsChangedEventArgs.Current"/> says, not cache.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    private SettingsService(ISettingsStore store, ILogger logger, AppSettings initial)
    {
        _store = store;
        _logger = logger;
        _current = initial;
    }

    /// <summary>Loads settings from durable storage (or defaults on first run) and constructs the service.</summary>
    public static SettingsService Load(ISettingsStore store, ILogger logger)
    {
        var loaded = store.Load();
        return new SettingsService(store, logger, loaded);
    }

    /// <summary>The currently-applied settings snapshot — the one and only authoritative value.</summary>
    public AppSettings Current
    {
        get { lock (_stateLock) { return _current; } }
    }

    /// <summary>
    /// Atomically transitions to <c>transform(current)</c>. Returns true on success;
    /// returns false (with <paramref name="error"/> set) if the persist+verify step fails —
    /// in which case in-memory state is rolled back to its pre-call value.
    /// </summary>
    public bool TryUpdate(Func<AppSettings, AppSettings> transform, out string? error)
    {
        AppSettings previous;
        AppSettings next;

        lock (_stateLock)
        {
            previous = _current;
            next = transform(previous);
            if (next == previous)
            {
                error = null;
                return true; // no-op — don't touch disk, don't fire events
            }
            _current = next;
        }
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));

        try
        {
            _store.SaveAndVerify(next);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            lock (_stateLock) { _current = previous; }
            Changed?.Invoke(this, new SettingsChangedEventArgs(next, previous));
            _logger.Error("Settings update failed — rolled back", new() { ["error"] = ex.Message });
            error = ex.Message;
            return false;
        }
    }
}
