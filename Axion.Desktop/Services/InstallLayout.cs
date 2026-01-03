using System;
using System.IO;

namespace Axion.Desktop.Services;

/// <summary>
/// Canonical Windows install layout (Variant 2):
///   C:\Axion\App\        (TradingBridgeApi publish)
///   C:\Axion\Launcher\   (launcher)
///   C:\Axion\Cache\      (temp zips, staging)
///   C:\Axion\Backup\     (rollback snapshots)
///   C:\Axion\Secrets\    (optional token files)
/// </summary>
public sealed class InstallLayout
{
    public required string RootDir { get; init; }
    public required string AppDir { get; init; }
    public required string LauncherDir { get; init; }
    public required string CacheDir { get; init; }
    public required string BackupDir { get; init; }
    public required string SecretsDir { get; init; }

    public void EnsureAll()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(LauncherDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(SecretsDir);
    }

    /// <summary>
    /// Detect install root based on where the launcher exe is located.
    /// Expected: C:\Axion\Launcher\Axion.exe  => root = C:\Axion
    /// Fallback: use %USERPROFILE%\Axion.
    /// </summary>
    public static InstallLayout DetectFromLauncherLocation()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // If we're in ...\Launcher\, root is one level up.
            var launcherDir = Path.GetFullPath(baseDir.TrimEnd(Path.DirectorySeparatorChar));
            var parent = Directory.GetParent(launcherDir);
            if (parent is not null)
            {
                var root = parent.FullName;
                return FromRoot(root);
            }
        }
        catch
        {
            // ignore
        }

        var fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Axion");
        return FromRoot(fallbackRoot);
    }

    public static InstallLayout FromRoot(string root)
    {
        root = Path.GetFullPath(root);
        return new InstallLayout
        {
            RootDir = root,
            AppDir = Path.Combine(root, "App"),
            LauncherDir = Path.Combine(root, "Launcher"),
            CacheDir = Path.Combine(root, "Cache"),
            BackupDir = Path.Combine(root, "Backup"),
            SecretsDir = Path.Combine(root, "Secrets"),
        };
    }
}
