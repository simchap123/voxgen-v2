using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.History;

/// <summary>One completed dictation, as kept in local history.</summary>
public sealed record HistoryEntry
{
    public required string Id { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string Text { get; init; }
    public string? RawText { get; init; }
    public int DurationMs { get; init; }

    /// <summary>File name of the saved WAV under the recordings folder, or null when audio wasn't saved.</summary>
    public string? AudioFile { get; init; }
}

/// <summary>
/// Local-only dictation history: one JSON object per line in history.jsonl, recordings as WAV
/// files alongside. Nothing leaves the machine. Append-only writes keep a crash from corrupting
/// earlier entries; unreadable lines are skipped on load.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _historyFile;
    private readonly string _recordingsDirectory;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();

    public HistoryStore(string historyFile, string recordingsDirectory, ILogger logger)
    {
        _historyFile = historyFile ?? throw new ArgumentNullException(nameof(historyFile));
        _recordingsDirectory = recordingsDirectory ?? throw new ArgumentNullException(nameof(recordingsDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Fired after an entry is added or history is cleared. May fire off the UI thread.</summary>
    public event EventHandler? Changed;

    public string RecordingsDirectory => _recordingsDirectory;

    /// <summary>
    /// Records a dictation. <paramref name="wavBytes"/> is written to the recordings folder when
    /// non-null; <paramref name="saveText"/> controls whether the transcript itself is kept.
    /// Returns the entry, or null if nothing was saved. Never throws — history is best-effort.
    /// </summary>
    public HistoryEntry? Add(string text, string? rawText, TimeSpan duration, byte[]? wavBytes, bool saveText)
    {
        if (!saveText && wavBytes is null) return null;

        HistoryEntry entry;
        lock (_ioLock)
        {
            var now = DateTime.UtcNow;
            var id = $"{now:yyyyMMdd-HHmmss-fff}";
            string? audioFile = null;

            if (wavBytes is not null)
            {
                try
                {
                    Directory.CreateDirectory(_recordingsDirectory);
                    audioFile = $"recording-{id}.wav";
                    File.WriteAllBytes(Path.Combine(_recordingsDirectory, audioFile), wavBytes);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to save recording to history", new() { ["error"] = ex.Message });
                    audioFile = null;
                }
            }

            entry = new HistoryEntry
            {
                Id = id,
                TimestampUtc = now,
                Text = saveText ? text : string.Empty,
                RawText = saveText ? rawText : null,
                DurationMs = (int)duration.TotalMilliseconds,
                AudioFile = audioFile,
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
                File.AppendAllText(_historyFile, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to append history entry", new() { ["error"] = ex.Message });
                return null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>Most recent entries first.</summary>
    public IReadOnlyList<HistoryEntry> LoadRecent(int max = 200)
    {
        lock (_ioLock)
        {
            if (!File.Exists(_historyFile)) return Array.Empty<HistoryEntry>();
            var entries = new List<HistoryEntry>();
            try
            {
                foreach (var line in File.ReadLines(_historyFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                        if (e is not null) entries.Add(e);
                    }
                    catch (JsonException) { /* skip a torn line */ }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to read history", new() { ["error"] = ex.Message });
            }
            return entries.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
        }
    }

    public string? AudioPath(HistoryEntry entry) =>
        entry.AudioFile is null ? null : Path.Combine(_recordingsDirectory, entry.AudioFile);

    /// <summary>
    /// Removes one entry (and its recording). Rewrites history.jsonl via a temp file so a crash
    /// mid-write can't lose the other entries. Returns false if the entry wasn't found or the
    /// rewrite failed.
    /// </summary>
    public bool Delete(string id)
    {
        var removed = false;
        lock (_ioLock)
        {
            if (!File.Exists(_historyFile)) return false;
            try
            {
                var kept = new List<string>();
                string? audioFile = null;
                foreach (var line in File.ReadLines(_historyFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    HistoryEntry? e = null;
                    try { e = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions); }
                    catch (JsonException) { /* keep torn lines out of the rewrite */ continue; }
                    if (e is not null && e.Id == id)
                    {
                        removed = true;
                        audioFile = e.AudioFile;
                        continue;
                    }
                    kept.Add(line);
                }
                if (!removed) return false;

                var tempPath = _historyFile + ".tmp";
                File.WriteAllLines(tempPath, kept);
                File.Replace(tempPath, _historyFile, destinationBackupFileName: null);

                if (audioFile is not null)
                {
                    try { File.Delete(Path.Combine(_recordingsDirectory, audioFile)); }
                    catch (Exception ex) { _logger.Warning("Failed to delete recording", new() { ["error"] = ex.Message }); }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to delete history entry", new() { ["id"] = id, ["error"] = ex.Message });
                return false;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Deletes the history file and every saved recording.</summary>
    public void Clear()
    {
        lock (_ioLock)
        {
            try { if (File.Exists(_historyFile)) File.Delete(_historyFile); }
            catch (Exception ex) { _logger.Warning("Failed to delete history file", new() { ["error"] = ex.Message }); }

            try
            {
                if (Directory.Exists(_recordingsDirectory))
                {
                    foreach (var f in Directory.EnumerateFiles(_recordingsDirectory, "*.wav")) File.Delete(f);
                }
            }
            catch (Exception ex) { _logger.Warning("Failed to delete recordings", new() { ["error"] = ex.Message }); }
        }
        _logger.Info("History cleared");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
