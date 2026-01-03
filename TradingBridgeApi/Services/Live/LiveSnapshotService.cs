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

    // базовий набір LIVE полів (UI-фільтри працюють тільки по LIVE)
    private static readonly string[] SnapshotFields =
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
        "AvPreMhv",
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

    public LiveSnapshotService(
        TradingAppClient client,
        UniverseService universe,
        ILogger<LiveSnapshotService> log)
    {
        _client = client;
        _universe = universe;
        _log = log;
    }

    // ✅ BACKWARD-COMPAT overload (this matches your call: GetSnapshotAsync(eligible, ct))
    public Task<(int UniverseCount, List<LiveSnapshotItemDto> Items)> GetSnapshotAsync(
        HashSet<string>? tickerAllow,
        CancellationToken ct)
    {
        return GetSnapshotAsync(tickerAllow, fieldsCsv: null, ct: ct);
    }

    // New overload with optional fieldsCsv for debugging/custom snapshots
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

        // allow overriding fields from query (debug)
        var fields = ParseFieldsOrDefault(fieldsCsv, SnapshotFields);

        Dictionary<string, Dictionary<string, object?>> quotes;
        try
        {
            // ✅ IMPORTANT: use async + DLL gate
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
