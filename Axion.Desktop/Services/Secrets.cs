using System;
using System.IO;

namespace Axion.Desktop.Services;

/// <summary>
/// Small helper for reading secrets.
/// Canon: keep tokens out of git. In production prefer Windows Credential Manager.
/// For a simple start we support:
///   C:\Axion\Secrets\app_github_pat.txt
/// and env var:
///   AXION_APP_GITHUB_TOKEN
/// </summary>
public static class Secrets
{
    public static string? TryReadGitHubTokenForAppRepo(InstallLayout install)
    {
        // 1) env
        var env = Environment.GetEnvironmentVariable("AXION_APP_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        // 2) file
        try
        {
            var p = Path.Combine(install.SecretsDir, "app_github_pat.txt");
            if (!File.Exists(p)) return null;
            var txt = File.ReadAllText(p).Trim();
            return string.IsNullOrWhiteSpace(txt) ? null : txt;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadGitHubTokenForSignalsRepo(InstallLayout install)
    {
        // 1) env
        var env = Environment.GetEnvironmentVariable("AXION_SIGNALS_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        // 2) file
        try
        {
            var p = Path.Combine(install.SecretsDir, "signals_github_pat.txt");
            if (!File.Exists(p)) return null;
            var txt = File.ReadAllText(p).Trim();
            return string.IsNullOrWhiteSpace(txt) ? null : txt;
        }
        catch
        {
            return null;
        }
    }
}
