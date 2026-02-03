using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using TradingBridgeApi.Signals; // ✅ ISignalsSource

namespace TradingBridgeApi.Services.Strategy.Chrono;

public sealed class ChronoFilesService
{
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(5);

    private readonly ISignalsSource _src;
    private readonly ILogger<ChronoFilesService> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // strategy code in manifest
    private const string StrategyCode = "chrono";

    // fallback dir if manifest missing
    private const string FallbackStrategyDir = "chrono";

    // filenames contract (canonical)
    private const string SummaryFile = "summary.csv";
    private const string OnefileFile = "onefile.jsonl";
    private const string BestFile = "best_params.jsonl";

    private const string ManifestRel = "_meta/manifest.json";

    // NEW: repo has no manifest; strategy folders are direct
    private const bool UseManifest = false;


    // manifest-derived
    private string _strategyDir = FallbackStrategyDir; // from manifest strategies[code].dir
    private DateTime? _manifestUpdatedAtUtc = null;     // from manifest.updatedAtUtc (single timestamp)
    private Dictionary<string, DateTime> _manifestFileTimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _manifestLoadedAtUtc = DateTime.MinValue;

    public ChronoFilesService(ISignalsSource src, ILogger<ChronoFilesService> log)
    {
        _src = src;
        _log = log;
    }

    /* ===================== Public API (same shape as before) ===================== */

    public async Task<(bool Ok, string? Error, string? ErrorType, object? Payload)> GetSummaryAsync(CancellationToken ct = default)
    {
        await EnsureManifestLoadedAsync(ct);
        return await ReadTextFileAsync(GetRelPath(SummaryFile), "SUMMARY_MISSING", "SUMMARY_READ_ERROR", ct);
    }

    public async Task<(bool Ok, string? Error, string? ErrorType, object? Payload)> GetTickerAsync(string ticker, CancellationToken ct = default)
    {
        await EnsureManifestLoadedAsync(ct);
        return await ReadJsonlByTickerAsync(GetRelPath(OnefileFile), StrategyCode, ticker, "ONEFILE_MISSING", "ONEFILE_READ_ERROR", ct);
    }

    public async Task<(bool Ok, string? Error, string? ErrorType, object? Payload)> GetBestParamsAsync(string ticker, CancellationToken ct = default)
    {
        await EnsureManifestLoadedAsync(ct);
        return await ReadJsonlByTickerAsync(GetRelPath(BestFile), StrategyCode, ticker, "BEST_PARAMS_MISSING", "BEST_PARAMS_READ_ERROR", ct);
    }

    /* ===================== Internals ===================== */

    private string GetRelPath(string fileName)
        => $"{_strategyDir.Trim('/').Trim('\\')}/{fileName}";

    private DateTime? TryManifestTime(string rel)
    {
        // preferred: exact per-file mapping if we have it
        if (_manifestFileTimes.TryGetValue(rel, out var dt))
            return dt;

        // tolerate leading "./" and slash variations
        var alt = rel.TrimStart('.', '/', '\\').Replace('\\', '/');
        if (_manifestFileTimes.TryGetValue(alt, out dt))
            return dt;

        // fallback: single updatedAtUtc for everything
        return _manifestUpdatedAtUtc;
    }

    private async Task<(bool Ok, string? Error, string? ErrorType, object? Payload)> ReadTextFileAsync(
        string relPath, string missingType, string readErrType, CancellationToken ct)
    {
        try
        {
            await using var s = await _src.OpenReadAsync(relPath, ct);
            using var sr = new StreamReader(s, Encoding.UTF8);

            var txt = await sr.ReadToEndAsync(ct);

            return (true, null, null, new
            {
                strategy = StrategyCode,
                updatedAt = TryManifestTime(relPath),
                text = txt
            });
        }
        catch (OperationCanceledException)
        {
            return (false, "request cancelled", "CANCELLED", null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed reading {RelPath} for {Strategy} from signals source", relPath, StrategyCode);

            if (ex is FileNotFoundException)
                return (false, $"file not found: {Path.GetFileName(relPath)}", missingType, null);

            return (false, $"cannot read {Path.GetFileName(relPath)}", readErrType, null);
        }
    }

    private async Task<(bool Ok, string? Error, string? ErrorType, object? Payload)> ReadJsonlByTickerAsync(
        string relPath,
        string strategy,
        string ticker,
        string missingType,
        string readErrType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            return (false, "ticker is empty", "TICKER_EMPTY", null);

        var want = ticker.Trim();

        try
        {
            await using var fs = await _src.OpenReadAsync(relPath, ct);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            while (!sr.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await sr.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                Dictionary<string, JsonElement>? obj;
                try { obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, JsonOpts); }
                catch { continue; }

                if (obj is null) continue;

                if (!TryGetString(obj, "ticker", out var t) && !TryGetString(obj, "Ticker", out t))
                    continue;

                if (string.Equals(t, want, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, null, null, new
                    {
                        strategy,
                        updatedAt = TryManifestTime(relPath),
                        ticker = want,
                        item = obj
                    });
                }
            }

            return (false, $"ticker '{want}' not found", "TICKER_NOT_FOUND", null);
        }
        catch (OperationCanceledException)
        {
            return (false, "request cancelled", "CANCELLED", null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed reading JSONL {RelPath} for {Strategy} from signals source", relPath, strategy);

            if (ex is FileNotFoundException)
                return (false, $"{Path.GetFileName(relPath)} not found", missingType, null);

            return (false, $"cannot read {Path.GetFileName(relPath)}", readErrType, null);
        }
    }

    private static bool TryGetString(Dictionary<string, JsonElement> obj, string key, out string? value)
    {
        value = null;
        if (!obj.TryGetValue(key, out var el)) return false;

        if (el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    /* ===================== Manifest ===================== */

    private async Task EnsureManifestLoadedAsync(CancellationToken ct)
    {
        if (_manifestLoadedAtUtc != DateTime.MinValue && (DateTime.UtcNow - _manifestLoadedAtUtc) < RefreshTtl)
            return;

        // NEW: No manifest in the new repo layout.
        // Keep all manifest logic below intact, but bypass it by default.
        if (!UseManifest)
        {
            _strategyDir = string.IsNullOrWhiteSpace(FallbackStrategyDir) ? StrategyCode : FallbackStrategyDir;
            _manifestUpdatedAtUtc = null;
            _manifestFileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _manifestLoadedAtUtc = DateTime.UtcNow;
            return;
        }

        // ===== old manifest logic (kept) =====
        var fileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var dir = FallbackStrategyDir;
        DateTime? updatedAt = null;

        try
        {
            await using var s = await _src.OpenReadAsync(ManifestRel, ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var root = doc.RootElement;

            // === Preferred (NEW) format ===
            // {
            //   "updatedAtUtc": "2026-01-02T13:30:00Z",
            //   "strategies": {
            //      "chrono": { "dir":"chrono", "files":["summary.csv","onefile.jsonl","best_params.jsonl"] }
            //   }
            // }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("updatedAtUtc", out var uEl) && uEl.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(uEl.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                        updatedAt = dt.ToUniversalTime();
                }

                if (root.TryGetProperty("strategies", out var strategies) && strategies.ValueKind == JsonValueKind.Object)
                {
                    if (strategies.TryGetProperty(StrategyCode, out var st) && st.ValueKind == JsonValueKind.Object)
                    {
                        if (st.TryGetProperty("dir", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                        {
                            var d = (dEl.GetString() ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(d))
                                dir = d.Replace('\\', '/').Trim('/');
                        }

                        if (st.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in fEl.EnumerateArray())
                            {
                                if (f.ValueKind != JsonValueKind.String) continue;
                                var name = (f.GetString() ?? "").Trim();
                                if (string.IsNullOrWhiteSpace(name)) continue;

                                var rel = $"{dir}/{name}";
                                if (updatedAt.HasValue)
                                    fileTimes[rel] = updatedAt.Value;
                            }
                        }
                    }
                }
            }

            // === Backward-compatible formats (OLDER) ===
            // 1) { "files": [ { "path": "...", "updatedAtUtc": "..." }, ... ] }
            // 2) { "files": { "chrono/onefile.jsonl": "2026-01-01T..." , ... } }
            // 3) { "chrono/onefile.jsonl": { "updatedAtUtc": "..." } , ... }
            if (fileTimes.Count == 0 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out var filesEl))
            {
                if (filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object) continue;

                        string? path = null;
                        if (f.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                            path = pEl.GetString();

                        if (string.IsNullOrWhiteSpace(path)) continue;

                        if (TryReadUpdatedAt(f, out var when))
                            fileTimes[path!] = when;
                    }
                }
                else if (filesEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in filesEl.EnumerateObject())
                    {
                        var path = prop.Name;
                        var v = prop.Value;

                        if (v.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                                fileTimes[path] = dt.ToUniversalTime();
                        }
                        else if (v.ValueKind == JsonValueKind.Object)
                        {
                            if (TryReadUpdatedAt(v, out var when))
                                fileTimes[path] = when;
                        }
                    }
                }
            }
            else if (fileTimes.Count == 0 && root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    var path = prop.Name;
                    var v = prop.Value;

                    if (v.ValueKind == JsonValueKind.Object && TryReadUpdatedAt(v, out var when))
                        fileTimes[path] = when;
                }
            }
        }
        catch
        {
            // manifest is optional; ignore
        }

        _strategyDir = string.IsNullOrWhiteSpace(dir) ? FallbackStrategyDir : dir;
        _manifestUpdatedAtUtc = updatedAt;
        _manifestFileTimes = fileTimes;
        _manifestLoadedAtUtc = DateTime.UtcNow;
    }


    private static bool TryReadUpdatedAt(JsonElement obj, out DateTime whenUtc)
    {
        whenUtc = default;

        var keys = new[] { "updatedAtUtc", "updated_at_utc", "updatedAt", "updated_at", "mtimeUtc", "mtime" };

        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var el)) continue;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                {
                    whenUtc = dt.ToUniversalTime();
                    return true;
                }
            }
        }

        return false;
    }
}
