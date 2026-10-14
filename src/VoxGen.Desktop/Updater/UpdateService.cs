using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Updater;

/// <summary>A newer release available on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string? Notes);

/// <summary>
/// One-click self-update for the portable single-file VoxGen.exe, driven by GitHub Releases.
///
/// Apply: download the new exe next to the running one, rename the running exe to
/// <c>VoxGen.exe.old</c> (Windows allows renaming a running image), move the download into place,
/// launch it with <see cref="AfterUpdateArg"/>, and let the caller shut down. The new process waits
/// for the singleton mutex and deletes the <c>.old</c> file on startup.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "simchap123/voxgen-v2";
    public const string AssetName = "VoxGen.exe";
    public const string AfterUpdateArg = "--after-update";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Version _current;

    public UpdateService(HttpClient http, Version current, ILogger logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Version CurrentVersion => _current;

    /// <summary>
    /// Self-update only works for the published single-file exe. A dev build (dotnet run, or a
    /// framework-dependent bin folder with VoxGen.dll beside it) is left alone.
    /// </summary>
    public static string? UpdatableExePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return null;
        if (!string.Equals(Path.GetFileName(path), AssetName, StringComparison.OrdinalIgnoreCase)) return null;
        var dir = Path.GetDirectoryName(path)!;
        if (File.Exists(Path.Combine(dir, "VoxGen.dll"))) return null;
        return path;
    }

    public bool CanSelfUpdate => UpdatableExePath() is not null;

    /// <summary>Returns the latest release if it is newer than the running version, else null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.TryAddWithoutValidation("User-Agent", "VoxGen-Updater");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct).ConfigureAwait(false);
        if (release?.TagName is null || release.Assets is null) return null;

        var latest = ParseVersion(release.TagName);
        if (latest is null || latest <= _current) return null;

        foreach (var asset in release.Assets)
        {
            if (string.Equals(asset.Name, AssetName, StringComparison.OrdinalIgnoreCase) && asset.Url is not null)
            {
                return new UpdateInfo(latest, release.TagName, asset.Url, release.Body);
            }
        }
        _logger.Warning("Latest release has no VoxGen.exe asset", new() { ["tag"] = release.TagName });
        return null;
    }

    /// <summary>
    /// Downloads and swaps in the new exe, then starts it. Returns true when the caller should shut
    /// down now. On any failure the original exe is restored and the error is rethrown.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = UpdatableExePath() ?? throw new InvalidOperationException("This build can't update itself.");
        var dir = Path.GetDirectoryName(exe)!;
        var download = Path.Combine(dir, AssetName + ".download");
        var old = exe + ".old";

        _logger.Info("Downloading update", new() { ["tag"] = update.Tag });
        using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(download);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total.Value);
            }
        }

        if (!LooksLikeExe(download))
        {
            TryDelete(download);
            throw new InvalidDataException("Downloaded update is not a valid program.");
        }

        TryDelete(old);
        File.Move(exe, old);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe); // put the original back
            throw;
        }

        Process.Start(new ProcessStartInfo { FileName = exe, Arguments = AfterUpdateArg, UseShellExecute = true });
        _logger.Info("Update applied; restarting", new() { ["tag"] = update.Tag });
        return true;
    }

    /// <summary>Removes the previous exe left behind by an update. Call on startup.</summary>
    public static void CleanupOldVersion()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        TryDelete(exe + ".old");
        TryDelete(Path.Combine(Path.GetDirectoryName(exe)!, AssetName + ".download"));
    }

    /// <summary>"v2.0.12-preview" → 2.0.12. Null when unparseable.</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 1_000_000) return false;
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private sealed record GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; init; }
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("assets")] public GitHubAsset[]? Assets { get; init; }
    }
}
