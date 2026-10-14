using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Settings;

/// <summary>
/// The user's Groq API key, DPAPI-encrypted (CurrentUser) in its own file. Kept out of
/// <see cref="AppSettings"/> so the secret never lands in plaintext settings.json or logs.
/// </summary>
public sealed class ApiKeyStore
{
    public const string GroqEntropy = "VoxGen.GroqApiKey.v1";
    public const string NvidiaEntropy = "VoxGen.NvidiaApiKey.v1";

    private readonly byte[] Entropy;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();
    private string? _cached;
    private bool _loaded;

    public ApiKeyStore(string path, ILogger logger, string entropy = GroqEntropy)
    {
        Entropy = Encoding.UTF8.GetBytes(entropy);
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Fired after the key is saved or removed.</summary>
    public event EventHandler? Changed;

    public bool HasKey => !string.IsNullOrEmpty(Get());

    /// <summary>Returns the stored key, or null when none is saved or the file is unreadable.</summary>
    public string? Get()
    {
        lock (_ioLock)
        {
            if (_loaded) return _cached;
            _loaded = true;
            if (!File.Exists(_path)) return _cached = null;
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain).Trim();
                return _cached = key.Length == 0 ? null : key;
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to read API key; treating as not set", new() { ["error"] = ex.Message });
                return _cached = null;
            }
        }
    }

    /// <summary>Encrypts and atomically writes the key. Throws on I/O failure so the UI can report it.</summary>
    public void Save(string key)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0) throw new ArgumentException("API key is empty.", nameof(key));

        lock (_ioLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tempPath = _path + ".tmp";
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tempPath, cipher);
            if (File.Exists(_path)) File.Replace(tempPath, _path, destinationBackupFileName: null);
            else File.Move(tempPath, _path);
            _cached = key;
            _loaded = true;
        }
        _logger.Info("API key saved");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_ioLock)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { _logger.Warning("Failed to delete API key file", new() { ["error"] = ex.Message }); }
            _cached = null;
            _loaded = true;
        }
        _logger.Info("API key removed");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"gsk_…abcd" — safe to show in the UI.</summary>
    public static string Mask(string key) =>
        key.Length <= 8 ? new string('•', key.Length) : $"{key[..4]}…{key[^4..]}";
}
