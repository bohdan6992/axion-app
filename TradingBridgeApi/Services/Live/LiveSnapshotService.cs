using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.Services.Live;

public sealed class LiveSnapshotService
{
    private readonly TradingAppClient _client;
    private readonly UniverseService _universe;
    private readonly ILogger<LiveSnapshotService> _log;

    // =========================
    // DEFAULT live fields (backward compatible)
    // =========================
    private static readonly string[] SnapshotFields_Default =
    {
        "BidLstClsΔ%",
        "AskLstClsΔ%",
        "Bid",
        "Ask",
        "LstPrcL",
        "LstCls",
        "YCls",
        "TCls",

        "ADV20",
        "ADV20NF",
        "ADV90",
        "ADV90NF",
        "AvPreMhVol90",
        "RoundLot",
        "VWAP",

        "PosSize",
        "ClsToCls%",
        "Lo",
        "LstClsNewsCnt",

        "Exchange",
        "TrdStatus",
        "MarketCapM",
        "PositionBp",
        "TierBP",
        "PreMhVolNF",
        "EquityType",
        "Dividend",
        "Country",
        "Company",
        "SectorL3",
        "NewsCnt",

        "IsPTP",
        "SSR",
        "OutThSSR",
        "Spread",
        "Report",
        "ETF",
    };

    // =========================
    // FULL TERMINAL live fields (for tape writer / terminal)
    // - safe superset: if some fields are missing in TRAP, they will just not appear in row dict
    // =========================
    private static readonly string[] SnapshotFields_FullTerminal =
    {
        // Quotes / pct
        "Bid", "Ask",
        "BidLstClsΔ%", "AskLstClsΔ%",
        "LstPrcLstClsΔ%",
        "LstPrcTOpenΔ%",
        "LstCls", "YCls", "TCls", "LstPrcL",
        "TOpen",
        "Lo",
        "Hi",
        "ATR14",
        "VWAP",
        "Spread",

        // ✅ Day OSB (TRAP готове; потрібно для Sifter / TickerDays)
        "Gap",
        "Gap%",
        "ClsToCls%",

        // Liquidity / vols
        "ADV20", "ADV20NF",
        "ADV90", "ADV90NF",
        "PreMhVolNF",
        "AvPreMhVol90",
        "RoundLot",

        // Meta
        "Exchange",
        "TrdStatus",
        "MarketCapM",
        "TierBP",
        "PositionBp",
        "PosSize",
        "EquityType",
        "Country",
        "Company",
        "SectorL3",
        "Dividend",

        // News/flags
        "NewsCnt",
        "LstClsNewsCnt",
        "IsPTP",
        "SSR",
        "OutThSSR",
        "Report",
        "ETF",

        // Bench (some TRAP builds provide these; else ignored)
        "Benchmark",
        "BenchBidLstClsΔ%",
        "BenchAskLstClsΔ%",
        "BenchBid",
        "BenchAsk",
        "BenchLstCls",
        "BenchYCls",

        // Arbitrage-ready (ONLY if upstream provides; else ignored)
        // We intentionally DO NOT compute these here.
        "ZapPctS",
        "ZapPctL",
        "SigmaZapS",
        "SigmaZapL",
        "TruePriceS",
        "TruePriceL",
        "ShortCandidate",
        "LongCandidate",
        "Sig",
    };

    public LiveSnapshotService(
        TradingAppClient client,
        UniverseService universe,
        ILogger<LiveSnapshotService> log)
    {
        _client = client;
        _universe = universe;
        _log = log;
    }

    // ✅ For tape writer: snapshot for ALL universe (default fields unless you ask FULL)
    public Task<(int UniverseCount, List<LiveSnapshotItemDto> Items)> GetSnapshotAsync(CancellationToken ct)
        => GetSnapshotAsync(tickerAllow: null, fieldsCsv: null, ct: ct);

    // ✅ BACKWARD-COMPAT overload (matches your call: GetSnapshotAsync(eligible, ct))
    public Task<(int UniverseCount, List<LiveSnapshotItemDto> Items)> GetSnapshotAsync(
        HashSet<string>? tickerAllow,
        CancellationToken ct)
    {
        return GetSnapshotAsync(tickerAllow, fieldsCsv: null, ct: ct);
    }

    /// <summary>
    /// Snapshot with optional tickerAllow and optional fieldsCsv.
    /// fieldsCsv semantics:
    /// - null/empty => Default fields (backward compatible)
    /// - "FULL"     => Full terminal superset (recommended for tape writer)
    /// - "a,b,c"    => custom list of fields
    /// </summary>
    public async Task<(int UniverseCount, List<LiveSnapshotItemDto> Items)> GetSnapshotAsync(
        HashSet<string>? tickerAllow,
        string? fieldsCsv = null,
        CancellationToken ct = default)
    {
        var universe = await _universe.LoadUniverseAsync(ct);
        if (universe.Count == 0)
            return (0, new List<LiveSnapshotItemDto>());

        IEnumerable<string> tickers = universe;

        if (tickerAllow is not null && tickerAllow.Count > 0)
            tickers = tickers.Where(t => tickerAllow.Contains(t));

        var list = tickers.ToList();
        if (list.Count == 0)
            return (universe.Count, new List<LiveSnapshotItemDto>());

        // fields preset logic
        var fields = ResolveFields(fieldsCsv);
        var isFull = IsFullPreset(fieldsCsv);

        Dictionary<string, Dictionary<string, object?>> quotes;
        try
        {
            // ✅ IMPORTANT:
            // - default snapshots (handlers) => single call (old behavior)
            // - FULL snapshots (tape writer) => batched call (safer for 4k universe)
            if (isFull)
                quotes = await _client.GetQuotesAsync(list.ToArray(), fields, TradingAppClient.DefaultBatchSize, ct);
            else
                quotes = await _client.GetQuotesAsync(list.ToArray(), fields, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LiveSnapshot: GetQuotesAsync failed");
            return (universe.Count, new List<LiveSnapshotItemDto>());
        }

        var items = new List<LiveSnapshotItemDto>(capacity: Math.Min(list.Count, 5000));

        foreach (var t in list)
        {
            if (!quotes.TryGetValue(t, out var row) || row is null)
                continue;

            items.Add(new LiveSnapshotItemDto
            {
                Ticker = t,
                Fields = row
            });
        }

        return (universe.Count, items);
    }

    /// <summary>
    /// Snapshot for an explicit list of tickers.
    /// Useful for TapeWriter when you want to request universe + benches (or any custom set)
    /// without loading UniverseService first.
    /// Returns (requestedTickerCount, items).
    /// Supports the same fieldsCsv presets as GetSnapshotAsync (null/FULL/custom CSV).
    /// </summary>
    public async Task<(int RequestedCount, List<LiveSnapshotItemDto> Items)> GetSnapshotForTickersAsync(
        IEnumerable<string> tickers,
        string? fieldsCsv = null,
        CancellationToken ct = default)
    {
        if (tickers is null)
            return (0, new List<LiveSnapshotItemDto>());

        var list = tickers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (list.Length == 0)
            return (0, new List<LiveSnapshotItemDto>());

        var fields = ResolveFields(fieldsCsv);
        var isFull = IsFullPreset(fieldsCsv);

        Dictionary<string, Dictionary<string, object?>> quotes;
        try
        {
            if (isFull)
                quotes = await _client.GetQuotesAsync(list, fields, TradingAppClient.DefaultBatchSize, ct);
            else
                quotes = await _client.GetQuotesAsync(list, fields, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LiveSnapshot (tickers): GetQuotesAsync failed");
            return (list.Length, new List<LiveSnapshotItemDto>());
        }

        var items = new List<LiveSnapshotItemDto>(capacity: Math.Min(list.Length, 5000));

        foreach (var t in list)
        {
            if (!quotes.TryGetValue(t, out var row) || row is null)
                continue;

            items.Add(new LiveSnapshotItemDto
            {
                Ticker = t,
                Fields = row
            });
        }

        return (list.Length, items);
    }

    private static bool IsFullPreset(string? fieldsCsv)
    {
        if (string.IsNullOrWhiteSpace(fieldsCsv))
            return false;

        var token = fieldsCsv.Trim();
        return string.Equals(token, "FULL", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "FULL_TERMINAL", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ResolveFields(string? fieldsCsv)
    {
        if (string.IsNullOrWhiteSpace(fieldsCsv))
            return SnapshotFields_Default;

        var token = fieldsCsv.Trim();

        if (string.Equals(token, "FULL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "FULL_TERMINAL", StringComparison.OrdinalIgnoreCase))
            return SnapshotFields_FullTerminal;

        // else treat as CSV
        return ParseFieldsOrDefault(token, SnapshotFields_Default);
    }

    private static string[] ParseFieldsOrDefault(string? fieldsCsv, string[] fallback)
    {
        if (string.IsNullOrWhiteSpace(fieldsCsv))
            return fallback;

        var fields = fieldsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return fields.Length == 0 ? fallback : fields;
    }
}
