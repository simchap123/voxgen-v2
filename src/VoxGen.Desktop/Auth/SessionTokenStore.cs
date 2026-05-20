using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Auth;

/// <summary>
/// At-rest storage for the Supabase session, DPAPI-protected per current user.
///
/// PRD §14.2 — the session token is stored with DPAPI, not as a setting.
/// Same atomic-write pattern as <see cref="Settings.JsonSettingsStore"/>: write a temp
/// file, then File.Replace into place so a crash mid-write never leaves a half-written file.
/// </summary>
public sealed class SessionTokenStore
{
    // The session file is small and machine-local; a fixed app-specific entropy ties the
    // ciphertext to VoxGen even if another DPAPI-using app on the same user account ran
    // ProtectedData.Unprotect on the file. Not a secret — defense-in-depth labelling.
    private static readonly byte[] Entropy = "VoxGen.Desktop.SessionTokenStore.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();

    public SessionTokenStore(string path, ILogger logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Serialize, DPAPI-protect, and atomically replace the on-disk session file.</summary>
    public void Save(SupabaseSession session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));

        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("Session token path has no directory.");
            Directory.CreateDirectory(directory);

            var tempPath = _path + ".tmp";
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
                var ciphertext = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(tempPath, ciphertext);

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
                _logger.Error("Failed to save session token", new()
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

    /// <summary>
    /// Read, DPAPI-unprotect, and deserialize. Returns <c>false</c> (without throwing) on
    /// missing/unreadable/corrupted files — the caller treats that as "not signed in" and
    /// the user re-signs-in. PRD §5.3 / §13 — never crash on bad local state.
    /// </summary>
    public bool TryLoad(out SupabaseSession? session)
    {
        lock (_ioLock)
        {
            session = null;

            if (!File.Exists(_path))
            {
                return false;
            }

            try
            {
                var ciphertext = File.ReadAllBytes(_path);
                var json = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                var loaded = JsonSerializer.Deserialize<SupabaseSession>(json, JsonOptions);
                if (loaded is null
                    || string.IsNullOrEmpty(loaded.AccessToken)
                    || string.IsNullOrEmpty(loaded.RefreshToken))
                {
                    _logger.Warning("Session token file deserialized to empty/invalid session",
                        new() { ["path"] = _path });
                    return false;
                }

                session = loaded;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to load session token; treating as signed-out", new()
                {
                    ["path"] = _path,
                    ["error"] = ex.Message,
                });
                return false;
            }
        }
    }

    /// <summary>Delete the on-disk session file if it exists. Idempotent.</summary>
    public void Clear()
    {
        lock (_ioLock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to clear session token", new()
                {
                    ["path"] = _path,
                    ["error"] = ex.Message,
                });
            }
        }
    }
}
