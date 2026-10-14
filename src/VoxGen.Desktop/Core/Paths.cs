using System;
using System.IO;

namespace VoxGen.Desktop.Core;

/// <summary>
/// Canonical filesystem locations for VoxGen. All user content lives under %APPDATA%\VoxGen — PRD §6.4.
/// </summary>
public static class Paths
{
    private static readonly string AppDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxGen");

    /// <summary>Root of all VoxGen user data (%APPDATA%\VoxGen). Used by uninstall to clean up.</summary>
    public static string AppDataDirectory => AppDataRoot;

    public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");
    /// <summary>The user's Groq API key, DPAPI-encrypted.</summary>
    public static string ApiKeyFile => Path.Combine(AppDataRoot, "groq-key.bin");
    /// <summary>The user's NVIDIA API key (read-aloud voice), DPAPI-encrypted.</summary>
    public static string NvidiaKeyFile => Path.Combine(AppDataRoot, "nvidia-key.bin");
    public static string HistoryFile => Path.Combine(AppDataRoot, "history.jsonl");
    public static string RecordingsDirectory => Path.Combine(AppDataRoot, "recordings");
    public static string TempAudioDirectory => Path.Combine(AppDataRoot, "temp-audio");
    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(TempAudioDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
    }
}
