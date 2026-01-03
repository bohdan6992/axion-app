using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingBridgeApi;

/// <summary>
/// Base file reader for signals/{strategy}/
/// Required files:
/// - summary.csv
/// - onefile.jsonl
/// - best_params.jsonl
///
/// This class is STREAMING-first (does not load whole files into memory).
/// </summary>
public abstract class StrategyFilesBase
{
    protected readonly IWebHostEnvironment Env;
    protected readonly ILogger Log;

    protected StrategyFilesBase(IWebHostEnvironment env, ILogger log)
    {
        Env = env;
        Log = log;
    }

    /// <summary>
    /// Folder name under ContentRootPath/signals/{StrategyKey}/
    /// Examples: "arbitrage", "chrono", "opendoor"
    /// </summary>
    protected abstract string StrategyKey { get; }

    protected string StrategyDir => Path.Combine(Env.ContentRootPath, "signals", StrategyKey);

    protected string SummaryPath => Path.Combine(StrategyDir, "summary.csv");
    protected string OnefilePath => Path.Combine(StrategyDir, "onefile.jsonl");
    protected string BestParamsPath => Path.Combine(StrategyDir, "best_params.jsonl");

    public DateTime SummaryUpdatedAtUtc => File.Exists(SummaryPath) ? File.GetLastWriteTimeUtc(SummaryPath) : DateTime.MinValue;
    public DateTime OnefileUpdatedAtUtc => File.Exists(OnefilePath) ? File.GetLastWriteTimeUtc(OnefilePath) : DateTime.MinValue;
    public DateTime BestParamsUpdatedAtUtc => File.Exists(BestParamsPath) ? File.GetLastWriteTimeUtc(BestParamsPath) : DateTime.MinValue;

    // =========================
    // Response DTO for summary
    // =========================

    public sealed class CsvSummaryResponse
    {
        public DateTime UpdatedAtUtc { get; init; }
        public int Count { get; init; }
        public List<string> Header { get; init; } = new();
        public List<Dictionary<string, string>> Items { get; init; } = new();
    }

    // =========================
    // SUMMARY (CSV)
    // =========================

    /// <summary>
    /// Reads signals/{strategy}/summary.csv.
    /// - Returns header (columns) + items as list of row dictionaries.
    /// - If q is provided, filters by ticker containing q (case-insensitive),
    ///   and also supports comma-separated tickers (AAPL,MSFT).
    /// </summary>
    public async Task<CsvSummaryResponse> GetSummaryAsync(string? q, CancellationToken ct)
    {
        var path = SummaryPath;
        if (!File.Exists(path))
        {
            Log.LogWarning("[{Strategy}] summary.csv not found at {Path}", StrategyKey, path);
            return new CsvSummaryResponse
            {
                UpdatedAtUtc = DateTime.MinValue,
                Count = 0,
                Header = new(),
                Items = new()
            };
        }

        var updatedAt = File.GetLastWriteTimeUtc(path);

        HashSet<string>? allowTickers = null;
        string? contains = null;

        if (!string.IsNullOrWhiteSpace(q))
        {
            var qq = q.Trim();
            if (qq.Contains(','))
            {
                allowTickers = qq
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.Trim().ToUpperInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (allowTickers.Count == 0) allowTickers = null;
            }
            else
            {
                contains = qq;
            }
        }

        // Simple CSV parser (supports quotes) for our files
        static List<string> ParseCsvLine(string line)
        {
            var res = new List<string>();
            if (line is null) return res;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                if (ch == '"')
                {
                    // Escaped quote
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
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
            return res;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);

        // header
        string? headerLine = await sr.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return new CsvSummaryResponse
            {
                UpdatedAtUtc = updatedAt,
                Count = 0
            };
        }

        var header = ParseCsvLine(headerLine);
        int tickerIdx = header.FindIndex(h => string.Equals(h, "ticker", StringComparison.OrdinalIgnoreCase));

        var items = new List<Dictionary<string, string>>(capacity: 1024);

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = ParseCsvLine(line);

            // normalize size
            if (cols.Count < header.Count)
            {
                while (cols.Count < header.Count) cols.Add("");
            }

            string? ticker = null;
            if (tickerIdx >= 0 && tickerIdx < cols.Count)
                ticker = cols[tickerIdx];

            // filter
            if (allowTickers is not null)
            {
                if (string.IsNullOrWhiteSpace(ticker)) continue;
                if (!allowTickers.Contains(ticker.Trim().ToUpperInvariant())) continue;
            }
            else if (!string.IsNullOrWhiteSpace(contains))
            {
                if (string.IsNullOrWhiteSpace(ticker)) continue;
                if (ticker.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                var key = header[i];
                var val = i < cols.Count ? cols[i] : "";
                row[key] = val;
            }

            items.Add(row);
        }

        return new CsvSummaryResponse
        {
            UpdatedAtUtc = updatedAt,
            Count = items.Count,
            Header = header,
            Items = items
        };
    }

    // =========================
    // ONEFILE (JSONL)
    // =========================

    /// <summary>
    /// Reads signals/{strategy}/onefile.jsonl and returns the first JSON object with matching ticker.
    /// Returns null if not found.
    /// </summary>
    public async Task<JsonElement?> GetOnefileTickerAsync(string ticker, CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return null;

        var path = OnefilePath;
        if (!File.Exists(path))
        {
            Log.LogWarning("[{Strategy}] onefile.jsonl not found at {Path}", StrategyKey, path);
            return null;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var raw = line.Trim();
            if (raw.StartsWith("#")) continue;

            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { continue; }

            if (el.ValueKind != JsonValueKind.Object) continue;

            if (!TryReadTicker(el, out var t)) continue;
            if (!string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase)) continue;

            // Return a CLONED element (safe after method exits)
            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        return null;
    }

    // =========================
    // BEST_PARAMS (JSONL)
    // =========================

    /// <summary>
    /// Reads signals/{strategy}/best_params.jsonl and returns the first JSON object with matching ticker.
    /// Returns null if not found.
    /// </summary>
    public async Task<JsonElement?> GetBestParamsTickerAsync(string ticker, CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return null;

        var path = BestParamsPath;
        if (!File.Exists(path))
        {
            Log.LogWarning("[{Strategy}] best_params.jsonl not found at {Path}", StrategyKey, path);
            return null;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);

        while (!sr.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var raw = line.Trim();
            if (raw.StartsWith("#")) continue;

            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { continue; }

            if (el.ValueKind != JsonValueKind.Object) continue;

            if (!TryReadTicker(el, out var t)) continue;
            if (!string.Equals(t, ticker, StringComparison.OrdinalIgnoreCase)) continue;

            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        return null;
    }

    /// <summary>
    /// Batch-read best_params rows for a set of tickers.
    /// Streaming scan over JSONL and returns only requested tickers.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> GetBestParamsForTickersAsync(
        IEnumerable<string> tickers,
        CancellationToken ct)
    {
        var set = tickers?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return result;

        var path = BestParamsPath;
        if (!File.Exists(path))
        {
            Log.LogWarning("[{Strategy}] best_params.jsonl not found at {Path}", StrategyKey, path);
            return result;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);

        while (!sr.EndOfStream && result.Count < set.Count)
        {
            ct.ThrowIfCancellationRequested();

            var line = await sr.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var raw = line.Trim();
            if (raw.StartsWith("#")) continue;

            JsonElement el;
            try { el = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { continue; }

            if (el.ValueKind != JsonValueKind.Object) continue;

            if (!TryReadTicker(el, out var t)) continue;
            if (!set.Contains(t)) continue;

            result[t] = JsonDocument.Parse(raw).RootElement.Clone();
        }

        return result;
    }

    // =========================
    // Helpers
    // =========================

    protected static bool TryReadTicker(JsonElement obj, out string ticker)
    {
        ticker = "";
        if (obj.ValueKind != JsonValueKind.Object) return false;

        if (obj.TryGetProperty("ticker", out var t1) && t1.ValueKind == JsonValueKind.String)
        {
            ticker = (t1.GetString() ?? "").Trim().ToUpperInvariant();
            return !string.IsNullOrWhiteSpace(ticker);
        }

        if (obj.TryGetProperty("Ticker", out var t2) && t2.ValueKind == JsonValueKind.String)
        {
            ticker = (t2.GetString() ?? "").Trim().ToUpperInvariant();
            return !string.IsNullOrWhiteSpace(ticker);
        }

        return false;
    }
}
