using System.Text;
using Microsoft.Extensions.Logging;

namespace TradingBridgeApi.Auth;

public sealed class AllowlistService
{
    private readonly ILogger<AllowlistService> _log;

    private readonly object _lock = new();

    private HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _mtimeUtc = DateTime.MinValue;
    private string _loadedFrom = "";

    public AllowlistService(ILogger<AllowlistService> log)
    {
        _log = log;
    }

    public bool IsAllowed(string? email)
    {
        var e = Normalize(email);
        if (e.Length == 0) return false;

        EnsureLoaded();

        lock (_lock)
            return _emails.Contains(e);
    }

    // (optional) helpful for quick debug
    public (string path, int count) GetState()
    {
        EnsureLoaded();
        lock (_lock) return (_loadedFrom, _emails.Count);
    }

    private void EnsureLoaded()
    {
        var path = AxionPaths.AllowlistPath;

        if (!File.Exists(path))
        {
            lock (_lock)
            {
                _emails = new(StringComparer.OrdinalIgnoreCase);
                _mtimeUtc = DateTime.MinValue;
                _loadedFrom = path;
            }

            _log.LogWarning("Allowlist file not found: {Path}", path);
            return;
        }

        var mtime = File.GetLastWriteTimeUtc(path);

        lock (_lock)
        {
            if (mtime == _mtimeUtc) return; // reload only when changed
        }

        var tmp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // UTF8 handles BOM fine
            foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = (rawLine ?? "").Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;

                // Header support
                if (line.Equals("email", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Take first column for csv/tsv/semicolon
                var first = line.Split(',', ';', '\t')[0].Trim().Trim('"');
                var norm = Normalize(first);
                if (norm.Length > 0)
                    tmp.Add(norm);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read allowlist: {Path}", path);
            tmp.Clear();
        }

        lock (_lock)
        {
            _emails = tmp;
            _mtimeUtc = mtime;
            _loadedFrom = path;
        }

        _log.LogInformation("Allowlist loaded: {Count} emails from {Path}", tmp.Count, path);
    }

    private static string Normalize(string? s)
        => (s ?? "").Trim().ToLowerInvariant();
}
