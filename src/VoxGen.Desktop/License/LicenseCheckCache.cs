using System;
using System.IO;
using System.Text.Json;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.License;

/// <summary>
/// Persists the last successful license validation so VoxGen keeps working when the
/// backend is briefly unreachable. PRD §8.12 / §13 / §21.4 — the grace applies to
/// license *checks*, not to transcription (which always needs connectivity).
///
/// Stored as plaintext JSON next to settings — it is non-sensitive trial/plan metadata,
/// not auth state. The auth state lives in <see cref="Auth.SessionTokenStore"/>.
///
/// Separate file from settings on purpose: settings are user-facing (PRD §10 — single
/// source of truth, write-verify cycle, etc.); the license cache is server-derived and
/// allowed to be lost without consequence.
/// </summary>
public sealed class LicenseCheckCache
{
    /// <summary>
    /// Default offline grace window for v1. PRD §21.4 — open decision: 7–14 days; 10 days
    /// is the v1 default until that decision lands. Document at call sites.
    /// </summary>
    public static readonly TimeSpan DefaultGraceWindow = TimeSpan.FromDays(10);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();

    public LicenseCheckCache(string path, ILogger logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Persist the latest successful validation. Best-effort: logs and rethrows on IO failure.</summary>
    public void Save(LicenseStatus status)
    {
        if (status is null) throw new ArgumentNullException(nameof(status));

        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("License cache path has no directory.");
            Directory.CreateDirectory(directory);

            var tempPath = _path + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(status, JsonOptions);
                File.WriteAllText(tempPath, json);

                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, _path);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to save license cache", new()
                {
                    ["path"] = _path,
                    ["error"] = ex.Message,
                });
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                }
                throw;
            }
        }
    }

    /// <summary>Returns <c>null</c> if no cache exists or the file is unreadable/corrupted.</summary>
    public LicenseStatus? Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<LicenseStatus>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to read license cache", new()
                {
                    ["path"] = _path,
                    ["error"] = ex.Message,
                });
                return null;
            }
        }
    }

    /// <summary>
    /// True iff a cached status exists AND it was validated within the supplied window
    /// (e.g. <see cref="DefaultGraceWindow"/>). Used to decide whether to keep the app
    /// usable while the backend is unreachable.
    /// </summary>
    public bool IsWithinOfflineGrace(TimeSpan window)
    {
        var cached = Load();
        if (cached is null) return false;
        return (DateTime.UtcNow - cached.ValidatedAtUtc) <= window;
    }
}
