using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VoxGen.Desktop.Logging;

/// <summary>
/// Append-only JSONL logger. One file per UTC day under %APPDATA%\VoxGen\logs.
/// Best-effort: writes that fail are swallowed — logging must never crash the app.
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _directory;
    private readonly object _writeLock = new();

    public FileLogger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? context = null)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            level = level.ToString(),
            message,
            context,
        };

        string line;
        try
        {
            line = JsonSerializer.Serialize(entry);
        }
        catch
        {
            // Serialization can fail if context holds an un-serializable type. Drop the context.
            line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                level = level.ToString(),
                message,
                context = "<unserializable>",
            });
        }

        var path = Path.Combine(_directory, $"voxgen-{DateTime.UtcNow:yyyy-MM-dd}.log");

        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best effort — nowhere to report a log failure.
        }
    }
}
