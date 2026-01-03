using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Axion.Desktop.Services;

/// <summary>
/// APP-A (Releases) updater.
/// - Downloads TradingBridgeApi publish zip from GitHub Releases (private repo supported via PAT)
/// - Validates zip
/// - Creates backup
/// - Replaces C:\Axion\App atomically (best-effort)
/// - Writes last updated release tag into Cache\app_version.json
/// </summary>
public sealed class GitHubReleaseAppUpdater
{
    public sealed record Result(bool Updated, string Tag);

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _assetName;
    private readonly Func<InstallLayout, string?> _tokenProvider;

    public GitHubReleaseAppUpdater(string owner, string repo, string assetName, Func<InstallLayout, string?> tokenProvider)
    {
        _owner = owner;
        _repo = repo;
        _assetName = assetName;
        _tokenProvider = tokenProvider;
    }

    public async Task<Result> UpdateApiAsync(InstallLayout install, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Repo: {_owner}/{_repo} (asset: {_assetName})");

        var token = _tokenProvider(install);
        if (string.IsNullOrWhiteSpace(token))
            progress?.Report("Warning: no GitHub token found (private repo will fail). Add AXION_APP_GITHUB_TOKEN or Secrets\\app_github_pat.txt");

        // 1) Get latest release
        var latest = await GetLatestReleaseAsync(token, ct);
        var tag = latest.TagName ?? "unknown";
        progress?.Report($"Latest release: {tag}");

        // 2) Check if already at this tag
        var currentTag = TryReadInstalledTag(install);
        if (!string.IsNullOrWhiteSpace(currentTag) && string.Equals(currentTag, tag, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("Already up-to-date.");
            return new Result(false, tag);
        }

        // 3) Find asset
        var asset = latest.Assets?.FirstOrDefault(a => string.Equals(a.Name, _assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            throw new InvalidOperationException($"Release {tag} has no asset named '{_assetName}'.");

        // 4) Download to Cache
        Directory.CreateDirectory(install.CacheDir);
        var zipPath = Path.Combine(install.CacheDir, $"{_assetName.Replace('.','_')}_{tag}.zip");
        progress?.Report("Downloading release asset...");
        await DownloadAsync(asset.BrowserDownloadUrl!, zipPath, token, progress, ct);

        // 5) Validate zip
        progress?.Report("Validating zip...");
        ValidateZip(zipPath);

        // 6) Extract to staging
        var stagingDir = Path.Combine(install.CacheDir, "new_app");
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);
        progress?.Report("Extracting...");
        ZipFile.ExtractToDirectory(zipPath, stagingDir);

        // Some zips contain a single root folder. If so, step into it.
        stagingDir = NormalizeExtractRoot(stagingDir);

        // 7) Backup current app dir
        var backupTag = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupDir = Path.Combine(install.BackupDir, $"App_{backupTag}");
        Directory.CreateDirectory(install.BackupDir);

        if (Directory.Exists(install.AppDir) && Directory.EnumerateFileSystemEntries(install.AppDir).Any())
        {
            progress?.Report("Creating backup...");
            CopyDirectory(install.AppDir, backupDir);
            WriteLastBackupPointer(install, backupDir);
        }

        // 8) Replace app dir atomically (best-effort on Windows)
        progress?.Report("Replacing C:\\Axion\\App...");
        ReplaceDirectory(install.AppDir, stagingDir);

        // 9) Record installed version
        WriteInstalledTag(install, tag);
        progress?.Report("Done.");

        return new Result(true, tag);
    }

    public static void TryRollbackLastBackup(InstallLayout install, IProgress<string>? progress)
    {
        try
        {
            var p = Path.Combine(install.CacheDir, "last_backup.txt");
            if (!File.Exists(p))
            {
                progress?.Report("No backup pointer found.");
                return;
            }

            var backupDir = File.ReadAllText(p).Trim();
            if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
            {
                progress?.Report("Backup folder missing.");
                return;
            }

            progress?.Report("Rolling back app folder...");
            ReplaceDirectory(install.AppDir, backupDir);
            progress?.Report("Rollback completed.");
        }
        catch (Exception ex)
        {
            progress?.Report("Rollback error: " + ex.Message);
        }
    }

    private async Task<ReleaseDto> GetLatestReleaseAsync(string? token, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AxionLauncher/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var resp = await http.GetAsync(url, ct);
        var txt = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub API error: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {txt}");

        return JsonSerializer.Deserialize<ReleaseDto>(txt, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse GitHub release json.");
    }

    private static async Task DownloadAsync(string url, string dest, string? token, IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AxionLauncher/1.0");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Asset download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
        progress?.Report($"Downloaded to {dest}");
    }

    private static void ValidateZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var hasExe = zip.Entries.Any(e => e.FullName.EndsWith("TradingBridgeApi.exe", StringComparison.OrdinalIgnoreCase));
        var hasDll = zip.Entries.Any(e => e.FullName.EndsWith("TradingBridgeApi.dll", StringComparison.OrdinalIgnoreCase));

        if (!hasExe && !hasDll)
            throw new InvalidOperationException("Zip does not contain TradingBridgeApi.exe nor TradingBridgeApi.dll");
    }

    private static string NormalizeExtractRoot(string extractedRoot)
    {
        // If extractedRoot contains exactly one directory and no files, step into it.
        var dirs = Directory.GetDirectories(extractedRoot);
        var files = Directory.GetFiles(extractedRoot);
        if (files.Length == 0 && dirs.Length == 1)
            return dirs[0];
        return extractedRoot;
    }

    private static void ReplaceDirectory(string targetDir, string sourceDir)
    {
        // Best-effort atomic-ish replace:
        //  - move current to *_old
        //  - move new into place
        //  - delete old
        var parent = Directory.GetParent(targetDir)?.FullName ?? throw new InvalidOperationException("Target dir has no parent");
        Directory.CreateDirectory(parent);

        var oldDir = targetDir + "_old";
        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);

        if (Directory.Exists(targetDir))
        {
            Directory.Move(targetDir, oldDir);
        }

        Directory.Move(sourceDir, targetDir);

        if (Directory.Exists(oldDir))
        {
            try { Directory.Delete(oldDir, true); } catch { /* ignore */ }
        }
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);

        foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, dir);
            Directory.CreateDirectory(Path.Combine(dstDir, rel));
        }

        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            var dest = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static void WriteInstalledTag(InstallLayout install, string tag)
    {
        Directory.CreateDirectory(install.CacheDir);
        var p = Path.Combine(install.CacheDir, "app_version.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new { tag, updatedUtc = DateTime.UtcNow.ToString("O") }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? TryReadInstalledTag(InstallLayout install)
    {
        try
        {
            var p = Path.Combine(install.CacheDir, "app_version.json");
            if (!File.Exists(p)) return null;
            var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("tag", out var t) ? t.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLastBackupPointer(InstallLayout install, string backupDir)
    {
        Directory.CreateDirectory(install.CacheDir);
        File.WriteAllText(Path.Combine(install.CacheDir, "last_backup.txt"), backupDir);
    }

    // DTOs (minimal)
    private sealed class ReleaseDto
    {
        public string? TagName { get; set; }
        public AssetDto[]? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        public string? Name { get; set; }
        public string? BrowserDownloadUrl { get; set; }
    }
}
