using System;
using System.IO;

namespace TradingBridgeApi;

public static class AxionPaths
{
    public static string AppDataRoot { get; private set; } = "";
    public static string SignalsRoot { get; private set; } = "";
    public static string AuthRoot => Path.Combine(AppDataRoot, "auth");
    public static string StateRoot => Path.Combine(AppDataRoot, "state");
    public static string StagingRoot => Path.Combine(AppDataRoot, "staging");
    public static string BackupRoot => Path.Combine(AppDataRoot, "backup");

    public static string AllowlistPath { get; private set; } = "";
    public static string UsersDbPath { get; private set; } = "";

    public static void InitFromEnvOrDefaults()
    {
        // 1) AppData root
        var envRoot = Environment.GetEnvironmentVariable("AXION_APPDATA_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            AppDataRoot = envRoot!;
        }
        else
        {
            AppDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Axion"
            );
        }

        // 2) Signals root
        var envSignals = Environment.GetEnvironmentVariable("AXION_SIGNALS_ROOT");
        SignalsRoot = !string.IsNullOrWhiteSpace(envSignals)
            ? envSignals!
            : Path.Combine(AppDataRoot, "signals");

        AllowlistPath = Path.Combine(AppDataRoot, "auth", "allowlist.csv");
        UsersDbPath = Path.Combine(AppDataRoot, "auth", "users.db");

        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(SignalsRoot);
        Directory.CreateDirectory(AuthRoot);
        Directory.CreateDirectory(StateRoot);
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(BackupRoot);

        if (!File.Exists(AllowlistPath))
            File.WriteAllText(AllowlistPath, "email\n");
    }
}
