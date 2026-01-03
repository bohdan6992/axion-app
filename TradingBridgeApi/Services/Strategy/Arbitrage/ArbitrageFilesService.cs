// Services/Strategy/Arbitrage/ArbitrageFilesService.cs
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using TradingBridgeApi.Signals; // ✅ ISignalsSource

namespace TradingBridgeApi.Services.Strategy.Arbitrage;

// =====================
// Contract
// =====================
public interface IArbitrageFilesService
{
    DateTime? OnefileUpdatedAtUtc { get; }
    DateTime? BestParamsUpdatedAtUtc { get; }

    Task<SummaryResponse> GetSummaryAsync(string? q, CancellationToken ct);

    Task<JsonElement?> GetOnefileTickerAsync(string ticker, CancellationToken ct);

    Task<JsonElement?> GetBestParamsTickerAsync(string ticker, CancellationToken ct);

    Task<Dictionary<string, JsonElement>> GetBestParamsForTickersAsync(IEnumerable<string> tickers, CancellationToken ct);

    Task<Dictionary<string, JsonElement>> GetBestParamsMapAsync(CancellationToken ct);

    Task<HashSet<string>> GetEligibleTickersAsync(string className, string type, double minRate, int minTotal, CancellationToken ct);
}

// =====================
// Implementation
// =====================
public sealed class ArbitrageFilesService : IArbitrageFilesService
{
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(5);

    private readonly ILogger<ArbitrageFilesService> _log;
    private readonly ISignalsSource _src;

    // strategy code in manifest
    private const string StrategyCode = "arbitrage";

    // fallback dir if manifest missing
    private const string FallbackStrategyDir = "arbitrage";

    // filenames contract (canonical)
    private const string SummaryFile = "summary.csv";
    private const string OnefileFile = "onefile.jsonl";
    private const string BestFile = "best_params.jsonl";

    private const string ManifestRel = "_meta/manifest.json";

    // manifest-derived
    private string _strategyDir = FallbackStrategyDir; // from manifest strategies[code].dir
    private DateTime? _manifestUpdatedAtUtc = null;     // from manifest.updatedAtUtc
    private Dictionary<string, DateTime> _manifestFileTimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _manifestLoadedAtUtc = DateTime.MinValue;

    // summary cache
    private Dictionary<string, SummaryRow> _summaryIndex = new(StringComparer.OrdinalIgnoreCase);
    private string[] _summaryHeader = Array.Empty<string>();
    private DateTime _summaryLoadedAtUtc = DateTime.MinValue;

    // best_params cache (ticker -> raw json line)
    private Dictionary<string, string> _bestRawIndex = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _bestLoadedAtUtc = DateTime.MinValue;

    public DateTime? OnefileUpdatedAtUtc => TryManifestTime(GetRelPath(OnefileFile));
    public DateTime? BestParamsUpdatedAtUtc => TryManifestTime(GetRelPath(BestFile));

    public ArbitrageFilesService(ILogger<ArbitrageFilesService> log, ISignalsSource src)
    {
        _log = log;
        _src = src;
    }

    /* ===================== Public API ===================== */

    public async Task<SummaryResponse> GetSummaryAsync(string? q, CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);
        await EnsureSummaryLoadedAsync(ct);

        IEnumerable<SummaryRow> rows = _summaryIndex.Values;

        if (!string.IsNullOrWhiteSpace(q))
        {
            var qq = q.Trim();
            rows = rows.Where(r => r.Ticker.Contains(qq, StringComparison.OrdinalIgnoreCase));
        }

        var items = rows
            .OrderBy(r => r.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SummaryResponse
        {
            UpdatedAtUtc = TryManifestTime(GetRelPath(SummaryFile)),
            Count = items.Count,
            Header = _summaryHeader,
            Items = items
        };
    }

    public async Task<JsonElement?> GetOnefileTickerAsync(string ticker, CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);
        return await FindJsonlByTickerAsync(GetRelPath(OnefileFile), ticker, ct);
    }

    public async Task<JsonElement?> GetBestParamsTickerAsync(string ticker, CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);
        await EnsureBestParamsLoadedAsync(ct);

        if (string.IsNullOrWhiteSpace(ticker))
            return null;

        var key = ticker.Trim();

        if (_bestRawIndex.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try { return JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { return null; }
        }

        return null;
    }

    public async Task<Dictionary<string, JsonElement>> GetBestParamsForTickersAsync(IEnumerable<string> tickers, CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);
        await EnsureBestParamsLoadedAsync(ct);

        var outMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tickers)
        {
            ct.ThrowIfCancellationRequested();

            var tt = (t ?? "").Trim();
            if (tt.Length == 0) continue;

            if (_bestRawIndex.TryGetValue(tt, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                try { outMap[tt] = JsonSerializer.Deserialize<JsonElement>(raw); }
                catch { /* ignore broken lines */ }
            }
        }

        return outMap;
    }

    public async Task<Dictionary<string, JsonElement>> GetBestParamsMapAsync(CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);
        await EnsureBestParamsLoadedAsync(ct);

        var outMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in _bestRawIndex)
        {
            ct.ThrowIfCancellationRequested();

            var ticker = (kv.Key ?? "").Trim();
            if (ticker.Length == 0) continue;

            var raw = kv.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            try { outMap[ticker] = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { /* ignore broken lines */ }
        }

        return outMap;
    }

    public async Task<HashSet<string>> GetEligibleTickersAsync(
        string className,
        string type,
        double minRate,
        int minTotal,
        CancellationToken ct)
    {
        await EnsureManifestLoadedAsync(ct);

        type = NormalizeType(type);
        className = NormalizeClass(className);

        // For Arbitrage we don't remap class names here (kept as-is, but normalized)
        var bestClass = MapClsForBest(className);

        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bestRel = GetRelPath(BestFile);

        await using var stream = await _src.OpenReadAsync(bestRel, ct);
        using var sr = new StreamReader(stream, Encoding.UTF8);

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var raw = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = raw.Trim();
            if (line.StartsWith("#")) continue;

            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!TryGetTicker(el, out var ticker)) continue;

            bool hasCounts = TryGetHardSoftCounts(el, bestClass, out var hardCnt, out var softCnt);
            if (!DominanceOk(type, hasCounts, hardCnt, softCnt))
                continue;

            // New format (ratings + hard_soft_share)
            if (TryGetRateAndTotal_NewFormat(el, bestClass, out var rateNew, out var totalNew))
            {
                if (totalNew >= minTotal && rateNew >= minRate)
                    eligible.Add(ticker!);
                continue;
            }

            // Legacy format support
            if (TryGetRateAndTotal_Legacy(el, className, type, out var rateOld, out var totalOld))
            {
                if (totalOld >= minTotal && rateOld >= minRate)
                    eligible.Add(ticker!);
            }
        }

        return eligible;
    }

    /* ===================== Manifest ===================== */

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

    private async Task EnsureManifestLoadedAsync(CancellationToken ct)
    {
        if (_manifestLoadedAtUtc != DateTime.MinValue && (DateTime.UtcNow - _manifestLoadedAtUtc) < RefreshTtl)
            return;

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
            //      "arbitrage": { "dir":"arbitrage", "files":["summary.csv","onefile.jsonl","best_params.jsonl"] }
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

                        // Success path: commit & return after assigning below
                    }
                }
            }

            // === Backward-compatible formats (OLDER) ===
            // 1) { "files": [ { "path": "...", "updatedAtUtc": "..." }, ... ] }
            // 2) { "files": { "arbitrage/onefile.jsonl": "2026-01-01T..." , ... } }
            // 3) { "arbitrage/onefile.jsonl": { "updatedAtUtc": "..." } , ... }
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
            // manifest is optional; don't fail endpoints
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

    /* ===================== best_params cache ===================== */

    private async Task EnsureBestParamsLoadedAsync(CancellationToken ct)
    {
        if (_bestRawIndex.Count > 0 && (DateTime.UtcNow - _bestLoadedAtUtc) < RefreshTtl)
            return;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bestRel = GetRelPath(BestFile);

        try
        {
            await using var fs = await _src.OpenReadAsync(bestRel, ct);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            while (!sr.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var raw = await sr.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var line = raw.Trim();
                if (line.StartsWith("#")) continue;

                JsonElement el;
                try { el = JsonSerializer.Deserialize<JsonElement>(line); }
                catch { continue; }

                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetTicker(el, out var ticker)) continue;

                dict[ticker!] = line;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cannot read {RelPath} from signals source", bestRel);
            _bestRawIndex.Clear();
            _bestLoadedAtUtc = DateTime.MinValue;
            return;
        }

        _bestRawIndex = dict;
        _bestLoadedAtUtc = DateTime.UtcNow;
    }

    /* ===================== Summary CSV ===================== */

    private async Task EnsureSummaryLoadedAsync(CancellationToken ct)
    {
        if (_summaryIndex.Count > 0 && (DateTime.UtcNow - _summaryLoadedAtUtc) < RefreshTtl)
            return;

        string[] lines;
        var summaryRel = GetRelPath(SummaryFile);

        try
        {
            await using var s = await _src.OpenReadAsync(summaryRel, ct);
            using var sr = new StreamReader(s, Encoding.UTF8);

            var all = await sr.ReadToEndAsync(ct);
            lines = all.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cannot read {RelPath} from signals source", summaryRel);
            _summaryIndex.Clear();
            _summaryHeader = Array.Empty<string>();
            _summaryLoadedAtUtc = DateTime.MinValue;
            return;
        }

        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            _summaryIndex.Clear();
            _summaryHeader = Array.Empty<string>();
            _summaryLoadedAtUtc = DateTime.UtcNow;
            return;
        }

        _summaryHeader = SplitCsvLine(lines[0])
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToArray();

        int Idx(string col) => Array.FindIndex(_summaryHeader, h => h.Equals(col, StringComparison.OrdinalIgnoreCase));
        string Get(string[] parts, int idx) => idx >= 0 && idx < parts.Length ? parts[idx].Trim() : "";

        var iTicker = Idx("ticker");
        var iBench = Idx("bench");
        var iCorr = Idx("corr");
        var iBeta = Idx("beta");
        var iSig = Idx("sig");

        var knownCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ticker","bench","corr","beta","sig"
        };

        var dict = new Dictionary<string, SummaryRow>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.TrimStart().StartsWith("#")) continue;

            var parts = SplitCsvLine(raw);

            var ticker = iTicker >= 0 ? Get(parts, iTicker) : "";
            if (string.IsNullOrWhiteSpace(ticker)) continue;

            var row = new SummaryRow
            {
                Ticker = ticker,
                Bench = iBench >= 0 ? Get(parts, iBench) : "",
                Corr = iCorr >= 0 ? Get(parts, iCorr) : "",
                Beta = iBeta >= 0 ? Get(parts, iBeta) : "",
                Sig = iSig >= 0 ? Get(parts, iSig) : ""
            };

            for (int c = 0; c < _summaryHeader.Length && c < parts.Length; c++)
            {
                var col = _summaryHeader[c];
                if (string.IsNullOrWhiteSpace(col)) continue;
                if (knownCols.Contains(col)) continue;

                row.Extras[col] = parts[c]?.Trim() ?? "";
            }

            dict[ticker] = row;
        }

        _summaryIndex = dict;
        _summaryLoadedAtUtc = DateTime.UtcNow;
    }

    private static string[] SplitCsvLine(string line)
    {
        if (line.IndexOf('"') < 0) return line.Split(',', StringSplitOptions.None);

        var res = new List<string>(64);
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                res.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        res.Add(sb.ToString());
        return res.ToArray();
    }

    /* ===================== JSONL helpers ===================== */

    private async Task<JsonElement?> FindJsonlByTickerAsync(string relPath, string ticker, CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim();
        if (ticker.Length == 0) return null;

        try
        {
            await using var fs = await _src.OpenReadAsync(relPath, ct);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            while (!sr.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var raw = await sr.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var line = raw.Trim();
                if (line.StartsWith("#")) continue;

                JsonElement el;
                try { el = JsonSerializer.Deserialize<JsonElement>(line); }
                catch { continue; }

                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetTicker(el, out var t)) continue;

                if (string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase))
                    return el;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cannot read {RelPath} from signals source", relPath);
        }

        return null;
    }

    private static bool TryGetTicker(JsonElement el, out string? ticker)
    {
        ticker = null;
        if (!el.TryGetProperty("ticker", out var tp)) return false;
        if (tp.ValueKind != JsonValueKind.String) return false;
        ticker = tp.GetString();
        return !string.IsNullOrWhiteSpace(ticker);
    }

    private static bool TryGetDouble(JsonElement el, out double value)
    {
        value = 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out value);

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement el, out int value)
    {
        value = 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out value);

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }

    private const decimal HsDominanceMult = 2.0m;

    private static bool TryGetHardSoftCounts(JsonElement bestParamsRow, string cls, out int hard, out int soft)
    {
        hard = 0;
        soft = 0;

        if (bestParamsRow.TryGetProperty("hard_soft_share", out var hsObj) && hsObj.ValueKind == JsonValueKind.Object)
        {
            if (hsObj.TryGetProperty(cls, out var clsObj) && clsObj.ValueKind == JsonValueKind.Object)
            {
                if (clsObj.TryGetProperty("hard", out var hEl) && TryGetInt(hEl, out var hh)) hard = hh;
                if (clsObj.TryGetProperty("soft", out var sEl) && TryGetInt(sEl, out var ss)) soft = ss;
                return true;
            }
        }

        if (bestParamsRow.TryGetProperty("hard_soft_counts", out var h2) && h2.ValueKind == JsonValueKind.Object)
        {
            if (h2.TryGetProperty(cls, out var clsObj2) && clsObj2.ValueKind == JsonValueKind.Object)
            {
                if (clsObj2.TryGetProperty("hard", out var hEl) && TryGetInt(hEl, out var hh)) hard = hh;
                if (clsObj2.TryGetProperty("soft", out var sEl) && TryGetInt(sEl, out var ss)) soft = ss;
                return true;
            }
        }

        return false;
    }

    private static bool DominanceOk(string type, bool hasCounts, int hard, int soft)
    {
        if (type.Equals("any", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!hasCounts) return false;

        var total = hard + soft;
        if (total <= 0) return false;

        if (type.Equals("hard", StringComparison.OrdinalIgnoreCase))
        {
            if (soft == 0) return hard > 0;
            return (decimal)hard >= (decimal)soft * HsDominanceMult;
        }

        // soft
        if (hard == 0) return soft > 0;
        return (decimal)soft >= (decimal)hard * HsDominanceMult;
    }

    private static bool TryGetRateAndTotal_NewFormat(JsonElement bestParamsRow, string cls, out double rate, out int total)
    {
        rate = 0;
        total = 0;

        if (!bestParamsRow.TryGetProperty("ratings", out var ratingsObj) || ratingsObj.ValueKind != JsonValueKind.Object)
            return false;

        if (!ratingsObj.TryGetProperty(cls, out var rEl))
            return false;

        if (rEl.ValueKind == JsonValueKind.Null)
            return false;

        if (!TryGetDouble(rEl, out rate))
            return false;

        if (bestParamsRow.TryGetProperty("hard_soft_share", out var hsObj) && hsObj.ValueKind == JsonValueKind.Object)
        {
            if (hsObj.TryGetProperty(cls, out var clsObj) && clsObj.ValueKind == JsonValueKind.Object)
            {
                int hard = 0, soft = 0;

                if (clsObj.TryGetProperty("hard", out var hEl) && TryGetInt(hEl, out var hh)) hard = hh;
                if (clsObj.TryGetProperty("soft", out var sEl) && TryGetInt(sEl, out var ss)) soft = ss;

                total = hard + soft;
                return true;
            }
        }

        // if we can't compute total, still treat as present but total=0
        total = 0;
        return true;
    }

    private static bool TryGetRateAndTotal_Legacy(JsonElement bestParamsRow, string className, string type, out double rate, out int total)
    {
        rate = 0;
        total = 0;

        if (!bestParamsRow.TryGetProperty("totals", out var totalsObj) || totalsObj.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement bucket;

        if (className.Equals("intra", StringComparison.OrdinalIgnoreCase))
        {
            if (!totalsObj.TryGetProperty("intra", out var intraObj) || intraObj.ValueKind != JsonValueKind.Object)
                return false;
            if (!intraObj.TryGetProperty("intra", out bucket) || bucket.ValueKind != JsonValueKind.Object)
                return false;
        }
        else
        {
            if (!totalsObj.TryGetProperty("pre", out var preObj) || preObj.ValueKind != JsonValueKind.Object)
                return false;
            if (!preObj.TryGetProperty(className, out bucket) || bucket.ValueKind != JsonValueKind.Object)
                return false;
        }

        if (!bucket.TryGetProperty("total", out var totalEl))
            return false;

        if (!TryGetInt(totalEl, out total))
            return false;

        var rateProp = type switch
        {
            "hard" => "rate_hard",
            "soft" => "rate_soft",
            _ => "rate_any"
        };

        if (!bucket.TryGetProperty(rateProp, out var rateEl))
            return false;

        if (!TryGetDouble(rateEl, out rate))
            return false;

        return true;
    }

    private static string NormalizeType(string type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "hard" => "hard",
            "soft" => "soft",
            _ => "any"
        };
    }

    private static string NormalizeClass(string c)
    {
        var s = (c ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "ark" => "ark",
            "print" => "print",
            "open" => "open",
            "intra" => "intra",
            "global" => "global",
            _ => "global"
        };
    }

    private static string MapClsForBest(string cls)
        => (cls ?? "global").Trim().ToLowerInvariant();
}
