using System;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Auth;

/// <summary>
/// Owns the signed-in Supabase session and is the single source the managed transcription provider
/// asks for an access token. Persists the session at rest (DPAPI via <see cref="SessionTokenStore"/>)
/// and refreshes the access token proactively before it expires (PRD §8.2, §8.12). This is the
/// "auth manager" Agent C flagged as the missing piece for the managed path.
/// </summary>
public sealed class SessionManager
{
    private readonly SupabaseAuth _auth;
    private readonly SessionTokenStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupabaseSession? _session;

    /// <summary>Raised when sign-in state changes (sign-in / sign-out). Token refreshes don't raise it.</summary>
    public event EventHandler? Changed;

    public SessionManager(SupabaseAuth auth, SessionTokenStore store, ILogger logger)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (_store.TryLoad(out var loaded)) _session = loaded;
    }

    public bool IsSignedIn => _session is not null;
    public string? Email => _session?.Email;

    public async Task SignInAsync(string email, string password, CancellationToken ct)
    {
        var session = await _auth.SignInAsync(email, password, ct).ConfigureAwait(false);
        SetSession(session);
        _logger.Info("Signed in", new() { ["email"] = session.Email });
    }

    public async Task SignUpAsync(string email, string password, CancellationToken ct)
    {
        var session = await _auth.SignUpAsync(email, password, ct).ConfigureAwait(false);
        SetSession(session);
        _logger.Info("Signed up", new() { ["email"] = session.Email });
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is not null)
        {
            try { await _auth.SignOutAsync(session.AccessToken, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warning("Remote sign-out failed; clearing locally anyway", new() { ["error"] = ex.Message }); }
        }
        _store.Clear();
        SetSession(null);
        _logger.Info("Signed out");
    }

    /// <summary>
    /// Returns a valid access token, refreshing it if it's within 60s of expiry. This is the delegate
    /// <see cref="Transcription.VoxGenManagedProvider"/> calls before each backend request.
    /// Throws <see cref="InvalidOperationException"/> if not signed in.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("Not signed in.");
            if (DateTime.UtcNow < session.ExpiresAtUtc.AddSeconds(-60))
            {
                return session.AccessToken;
            }

            _logger.Info("Refreshing Supabase access token");
            var refreshed = await _auth.RefreshAsync(session.RefreshToken, ct).ConfigureAwait(false);
            _session = refreshed;
            try { _store.Save(refreshed); } catch (Exception ex) { _logger.Error("Failed to persist refreshed session", new() { ["error"] = ex.Message }); }
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetSession(SupabaseSession? session)
    {
        _session = session;
        if (session is not null)
        {
            try { _store.Save(session); }
            catch (Exception ex) { _logger.Error("Failed to persist session", new() { ["error"] = ex.Message }); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
