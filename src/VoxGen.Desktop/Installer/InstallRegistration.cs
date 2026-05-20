using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using VoxGen.Desktop.Core;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Installer;

/// <summary>
/// Makes the portable single-file <c>VoxGen.exe</c> appear in Windows "Installed apps" /
/// "Programs and Features" without shipping a real installer, by writing a per-user uninstall
/// entry on first run (<c>HKCU\…\Uninstall\VoxGen</c>). Per-user → no admin prompt, and it keeps
/// the "just download the .exe" experience the product wants (PRD §15 branding, §6.5 /Installer).
///
/// The registered <c>UninstallString</c> is <c>"VoxGen.exe --uninstall"</c>; <see cref="Uninstall"/>
/// removes the registry entry, deletes <c>%APPDATA%\VoxGen</c>, and self-deletes the exe.
/// </summary>
public static class InstallRegistration
{
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VoxGen";
    private const string ExeName = "VoxGen.exe";
    private const string DisplayName = "VoxGen";
    private const string Publisher = "VoxGen";
    private const string AboutUrl = "https://github.com/simchap123/voxgen-v2";

    /// <summary>CLI flag that triggers uninstall. Matches what we write into <c>UninstallString</c>.</summary>
    public const string UninstallArg = "--uninstall";

    /// <summary>
    /// Ensure a per-user uninstall entry exists and points at the current exe. Idempotent and
    /// best-effort: any failure is logged and swallowed (registration is a nicety, never fatal).
    /// No-ops when not running as the published <c>VoxGen.exe</c> (e.g. <c>dotnet run</c>, tests,
    /// or a debug bin/ build) so dev machines don't get a bogus entry.
    /// </summary>
    public static void EnsureRegistered(string version, ILogger logger)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!IsRegisterableExe(exePath))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true);
            if (key is null) return;

            // Only rewrite when missing or the exe moved/updated — avoids touching the registry
            // on every single launch.
            var sameLocation = string.Equals(key.GetValue("DisplayIcon") as string, exePath, StringComparison.OrdinalIgnoreCase);
            var sameVersion = string.Equals(key.GetValue("DisplayVersion") as string, version, StringComparison.Ordinal);
            if (sameLocation && sameVersion)
            {
                return;
            }

            var installDir = Path.GetDirectoryName(exePath!) ?? string.Empty;
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", exePath!);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exePath}\" {UninstallArg}");
            key.SetValue("QuietUninstallString", $"\"{exePath}\" {UninstallArg}");
            key.SetValue("URLInfoAbout", AboutUrl);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try
            {
                var sizeKb = (int)(new FileInfo(exePath!).Length / 1024);
                key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
            }
            catch { /* size is cosmetic */ }

            logger.Info("Registered uninstall entry", new() { ["exe"] = exePath!, ["version"] = version });
        }
        catch (Exception ex)
        {
            logger.Warning("Failed to register uninstall entry", new() { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// Tear down everything the app created — kill any running instance, remove the uninstall
    /// registry entry and <c>%APPDATA%\VoxGen</c>, then schedule the exe to delete itself once this
    /// process exits. Called from the <c>--uninstall</c> startup branch before any UI is created.
    /// </summary>
    public static void Uninstall()
    {
        KillOtherInstances();

        // Remove the registry entry first so it leaves "Installed apps" immediately, even if a
        // later step fails.
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
        catch { /* best effort */ }

        // Remove all local user data (settings, logs, session token, models, temp audio).
        try
        {
            if (Directory.Exists(Paths.AppDataDirectory))
            {
                Directory.Delete(Paths.AppDataDirectory, recursive: true);
            }
        }
        catch { /* some files may still be locked; best effort */ }

        // A running exe can't delete itself, so spawn a detached cmd that waits ~2s for this
        // process to exit, then deletes the file. (`ping -n 3` ≈ 2s; works without a console.)
        try
        {
            var exePath = Environment.ProcessPath;
            if (IsRegisterableExe(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{exePath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
        }
        catch { /* if self-delete can't be scheduled, the entry + data are already gone */ }
    }

    private static bool IsRegisterableExe(string? exePath) =>
        !string.IsNullOrEmpty(exePath)
        && string.Equals(Path.GetFileName(exePath), ExeName, StringComparison.OrdinalIgnoreCase)
        // Skip dev build outputs so `dotnet run` / F5 don't register a throwaway entry.
        && !exePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static void KillOtherInstances()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("VoxGen").Where(p => p.Id != me))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                catch { /* may have already exited */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* best effort */ }
    }
}
