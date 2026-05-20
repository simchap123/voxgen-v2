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
    public static string SessionTokenFile => Path.Combine(AppDataRoot, "session.bin");
    public static string LicenseCacheFile => Path.Combine(AppDataRoot, "license.json");
    public static string HistoryDatabase => Path.Combine(AppDataRoot, "history.db");
    public static string TempAudioDirectory => Path.Combine(AppDataRoot, "temp-audio");
    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");

    /// <summary>Local STT model files (dev stopgap — Whisper ggml). Not used by the shipped managed path.</summary>
    public static string ModelsDirectory => Path.Combine(AppDataRoot, "models");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(TempAudioDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
    }
}
