using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Settings;

/// <summary>
/// JSON-on-disk settings store. Writes go through a temp file + verify-read + File.Replace so a
/// crashed or interrupted write never leaves a half-written settings file in place (PRD §10 rule 9).
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();

    public JsonSettingsStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public AppSettings Load()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_path))
            {
                _logger.Info("Settings file missing — using defaults", new() { ["path"] = _path });
                return AppSettings.Defaults;
            }

            try
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                             ?? throw new InvalidDataException("Settings file deserialized to null.");
                _logger.Info("Settings loaded", new()
                {
                    ["path"] = _path,
                    ["schemaVersion"] = loaded.SettingsSchemaVersion,
                });
                return loaded;
            }
            catch (Exception ex)
            {
                // PRD §10 — must not silently fall back to defaults; surface so the user keeps their file.
                _logger.Error("Failed to load settings", new()
                {
                    ["path"] = _path,
                    ["error"] = ex.Message,
                });
                throw;
            }
        }
    }

    public void SaveAndVerify(AppSettings settings)
    {
        lock (_ioLock)
        {
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("Settings path has no directory.");
            Directory.CreateDirectory(directory);

            var tempPath = _path + ".tmp";
            var backupPath = _path + ".bak";

            try
            {
                // 1. Serialize to a temp file.
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tempPath, json);

                // 2. Verify by reading back the temp file and checking value equality.
                var roundTripped = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(tempPath), JsonOptions)
                                   ?? throw new InvalidDataException("Verify read returned null.");
                if (roundTripped != settings)
                {
                    throw new InvalidDataException(
                        "Verify read did not match the settings just written — the JSON model is lossy.");
                }

                // 3. Atomic swap. File.Replace handles the previous-file backup in one call.
                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, backupPath);
                }
                else
                {
                    File.Move(tempPath, _path);
                }

                _logger.Debug("Settings persisted and verified", new() { ["path"] = _path });
            }
            catch (Exception ex)
            {
                _logger.Error("Settings persist/verify failed", new()
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
}
