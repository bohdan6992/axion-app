// StrategyCommon/StrategySignalService.cs
using System.Globalization;
using System.Text.Json;
using TradingBridgeApi.Services.Live;
using TradingBridgeApi.Services.Strategy.Arbitrage;
using TradingBridgeApi.StrategyCommon.Dtos;
using TradingBridgeApi.StrategyCommon.Signals;

namespace TradingBridgeApi.StrategyCommon;

public sealed class StrategySignalService
{
    private readonly LiveSnapshotService _live;
    private readonly ArbitrageFilesService _arbFiles;
    private readonly StrategyJoiner _joiner;
    private readonly EligibilityPolicy _elig;

    // universe ticker->bench map
    private readonly UniverseService _universe;

    public StrategySignalService(
        LiveSnapshotService live,
        ArbitrageFilesService arbFiles,
        StrategyJoiner joiner,
        EligibilityPolicy elig,
        UniverseService universe)
    {
        _live = live;
        _arbFiles = arbFiles;
        _joiner = joiner;
        _elig = elig;
        _universe = universe;
    }

    // Universal entrypoint for controllers
    public Task<SignalsResponseDto> GetSignalsAsync(SignalsQueryDto q, CancellationToken ct)
    {
        var strategy = (q.Strategy ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(strategy))
            strategy = "arbitrage";

        Console.WriteLine($"[SIG] GetSignalsAsync strategy='{strategy}'");

        if (strategy == "arbitrage")
            return GetArbitrageSignalsAsync(q, ct);

        Console.WriteLine($"[SIG] strategy '{strategy}' not wired yet -> empty response");

        return Task.FromResult(new SignalsResponseDto
        {
            Strategy = strategy,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UniverseTickers = 0,
            ReturnedTickers = 0,
            Items = new List<SignalItemDto>()
        });
    }

    public async Task<SignalsResponseDto> GetArbitrageSignalsAsync(SignalsQueryDto q, CancellationToken ct)
    {
        // ----------------------------
        // Normalize inputs
        // ----------------------------
        q.Class = (q.Class ?? "global").Trim().ToLowerInvariant();
        q.Type = (q.Type ?? "any").Trim().ToLowerInvariant();
        q.Mode = (q.Mode ?? "all").Trim().ToLowerInvariant();

        Console.WriteLine(
            $"[ARB] IN q: cls={q.Class} type={q.Type} mode={q.Mode} " +
            $"minRate={q.MinRate} minTotal={q.MinTotal} " +
            $"offset={(q.Offset.HasValue ? q.Offset.Value.ToString() : "null")} " +
            $"limit={(q.Limit.HasValue ? q.Limit.Value.ToString() : "null")} " +
            $"topN={q.TopN} " +
            $"tickers='{(q.Tickers ?? "")}'"
        );

        // ----------------------------
        // 0) universe map (ticker -> bench)
        // ----------------------------
        Dictionary<string, string> benchByTicker;
        try
        {
            benchByTicker = await _universe.LoadUniverseWithBenchAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ARB] ERROR in UniverseService.LoadUniverseWithBenchAsync: {ex}");
            benchByTicker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // ----------------------------
        // 1) eligible tickers (STATIC gate)
        // ----------------------------
        HashSet<string> eligible;
        try
        {
            eligible = await _arbFiles.GetEligibleTickersAsync(
                q.Class,
                q.Type,
                (double)q.MinRate,
                q.MinTotal,
                ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ARB] ERROR in GetEligibleTickersAsync: {ex}");
            throw;
        }

        Console.WriteLine($"[ARB] eligible={eligible.Count}");

        if (eligible.Count == 0)
        {
            Console.WriteLine("[ARB] STOP: eligible=0 -> returning empty");
            return new SignalsResponseDto
            {
                Strategy = "arbitrage",
                GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UniverseTickers = 0,
                ReturnedTickers = 0,
                Items = new List<SignalItemDto>()
            };
        }

        // ----------------------------
        // 1.5) build LIVE ticker set: eligible + benches
        // ----------------------------
        var needForLive = new HashSet<string>(eligible, StringComparer.OrdinalIgnoreCase);

        int addedBenches = 0;
        foreach (var t in eligible)
        {
            if (benchByTicker.TryGetValue(t, out var b) && !string.IsNullOrWhiteSpace(b))
            {
                if (needForLive.Add(b.Trim().ToUpperInvariant()))
                    addedBenches++;
            }
        }

        Console.WriteLine($"[ARB] needForLive={needForLive.Count} (added benches={addedBenches})");

        // ----------------------------
        // 2) LIVE snapshot for needForLive (tuple)
        // ----------------------------
        int universeCount;
        List<LiveSnapshotItemDto> liveItems;

        try
        {
            (universeCount, liveItems) = await _live.GetSnapshotAsync(needForLive, ct);
            liveItems ??= new List<LiveSnapshotItemDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ARB] ERROR in LiveSnapshotService.GetSnapshotAsync: {ex}");
            throw;
        }

        Console.WriteLine($"[ARB] liveItems(all)={liveItems.Count} universeCount={universeCount}");

        if (liveItems.Count == 0)
        {
            Console.WriteLine("[ARB] STOP: liveItems=0 -> returning empty");
            return new SignalsResponseDto
            {
                Strategy = "arbitrage",
                GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UniverseTickers = universeCount,
                ReturnedTickers = 0,
                Items = new List<SignalItemDto>()
            };
        }

        // Build map ticker -> fields for quick bench lookup
        var liveFieldsByTicker = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var li in liveItems)
        {
            var t = (li.Ticker ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) continue;
            if (li.Fields is null) continue;
            liveFieldsByTicker[t] = li.Fields;
        }

        // Filter live items back to eligible tickers for joining
        var eligibleLiveItems = liveItems
            .Where(x => x.Ticker is not null && eligible.Contains(x.Ticker.Trim().ToUpperInvariant()))
            .ToList();

        Console.WriteLine($"[ARB] eligibleLiveItems={eligibleLiveItems.Count}");

        if (eligibleLiveItems.Count == 0)
        {
            Console.WriteLine("[ARB] STOP: eligibleLiveItems=0 -> returning empty");
            return new SignalsResponseDto
            {
                Strategy = "arbitrage",
                GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UniverseTickers = universeCount,
                ReturnedTickers = 0,
                Items = new List<SignalItemDto>()
            };
        }

        // ----------------------------
        // ✅ CRITICAL FIX:
        // Enrich EACH eligible stock row with benchmark fields BEFORE joiner runs.
        // Otherwise Joiner computes BidBench/AskBench as null and STruePrice/ZAP break.
        // We store bench devs in PLAIN keys (BenchBidLstClsΔ%/BenchAskLstClsΔ%) and also keep bench.* for debugging.
        // ----------------------------
        int enriched = 0;
        int missingBench = 0;
        int missingBenchQuotes = 0;

        foreach (var li in eligibleLiveItems)
        {
            var t = (li.Ticker ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) continue;

            li.Fields ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!benchByTicker.TryGetValue(t, out var b) || string.IsNullOrWhiteSpace(b))
            {
                missingBench++;
                continue;
            }

            var bench = b.Trim().ToUpperInvariant();

            if (!liveFieldsByTicker.TryGetValue(bench, out var bf) || bf is null)
            {
                // benchmark was requested, but not returned by live snapshot (or field map not built)
                missingBenchQuotes++;
                // still write bench.ticker so UI sees mapping
                li.Fields["bench.ticker"] = bench;
                li.Fields["Benchmark"] = bench;
                continue;
            }

            // Always keep benchmark label in row so Joiner can read it right away.
            li.Fields["bench.ticker"] = bench;
            li.Fields["Benchmark"] = bench;

            // ✅ The joiner should read these FIRST (you will add this in Joiner or already did):
            // BidBench/AskBench := BenchBidLstClsΔ% / BenchAskLstClsΔ%
            li.Fields["BenchBidLstClsΔ%"] = TryGetObj(bf, "BidLstClsΔ%");
            li.Fields["BenchAskLstClsΔ%"] = TryGetObj(bf, "AskLstClsΔ%");

            // Optional extra bench fields (useful for UI/debugging)
            li.Fields["bench.Bid"] = TryGetObj(bf, "Bid");
            li.Fields["bench.Ask"] = TryGetObj(bf, "Ask");
            li.Fields["bench.LstCls"] = TryGetObj(bf, "LstCls");
            li.Fields["bench.YCls"] = TryGetObj(bf, "YCls");

            // Also keep the original composite keys you used (debug/back-compat)
            li.Fields["bench.BidLstClsΔ%"] = TryGetObj(bf, "BidLstClsΔ%");
            li.Fields["bench.AskLstClsΔ%"] = TryGetObj(bf, "AskLstClsΔ%");

            enriched++;
        }

        Console.WriteLine($"[ARB] bench-enrich: enriched={enriched} missingBench={missingBench} missingBenchQuotes={missingBenchQuotes}");

        // shape for joiner
        var liveSnapshot = new { items = eligibleLiveItems };

        // ----------------------------
        // 3) best_params for returned tickers only
        // ----------------------------
        var tickers = eligibleLiveItems
            .Select(x => (x.Ticker ?? "").Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"[ARB] tickers(from eligible live)={tickers.Length} first5={string.Join(",", tickers.Take(5))}");

        Dictionary<string, JsonElement> bestRaw;
        try
        {
            bestRaw = await _arbFiles.GetBestParamsForTickersAsync(tickers, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ARB] ERROR in GetBestParamsForTickersAsync: {ex}");
            throw;
        }

        Console.WriteLine($"[ARB] bestRaw={bestRaw.Count}");

        // ----------------------------
        // 4) map raw -> BestParamsDto
        // ----------------------------
        var bestMap = new Dictionary<string, BestParamsDto>(StringComparer.OrdinalIgnoreCase);

        int mapped = 0, skipped = 0;
        foreach (var kv in bestRaw)
        {
            var t = (kv.Key ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) { skipped++; continue; }

            if (TryMapBestParams(kv.Value, q.Class, q.Type, out var dto) && dto is not null)
            {
                bestMap[t] = dto;
                mapped++;
            }
            else
            {
                skipped++;
            }
        }

        Console.WriteLine($"[ARB] bestMap={bestMap.Count} mapped={mapped} skipped={skipped}");

        // ----------------------------
        // 5) join (NOW bench fields exist in row BEFORE this)
        // ----------------------------
        var joined = _joiner.Join(liveSnapshot, bestMap, "arbitrage", q).ToList();
        Console.WriteLine($"[ARB] joined={joined.Count}");

        // ✅ Keep Benchmark string consistent (joiner might set it from bench.ticker already)
        foreach (var it in joined)
        {
            var t = (it.Ticker ?? "").Trim().ToUpperInvariant();
            if (t.Length == 0) continue;

            if (benchByTicker.TryGetValue(t, out var b) && !string.IsNullOrWhiteSpace(b))
                it.Benchmark = b.Trim().ToUpperInvariant();
        }

        // ----------------------------
        // 6) eligibility floors again
        // ----------------------------
        var eligibleItems = _elig.Apply(joined, q).ToList();
        Console.WriteLine($"[ARB] after EligibilityPolicy.Apply => eligibleItems={eligibleItems.Count}");

        // ----------------------------
        // 7) paging
        // ----------------------------
        var offset = q.Offset.GetValueOrDefault(0);
        if (offset < 0) offset = 0;

        var limit = q.Limit.GetValueOrDefault(q.TopN);
        if (limit <= 0) limit = 100; // critical guard

        Console.WriteLine($"[ARB] paging: offset={offset} limit={limit} (q.limit={q.Limit?.ToString() ?? "null"} q.topN={q.TopN})");

        var finalItems = eligibleItems
            .Skip(offset)
            .Take(limit)
            .ToList();

        Console.WriteLine($"[ARB] finalItems={finalItems.Count}");

        return new SignalsResponseDto
        {
            Strategy = "arbitrage",
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UniverseTickers = universeCount,
            ReturnedTickers = finalItems.Count,
            Items = finalItems
        };
    }

    private static object? TryGetObj(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) ? v : null;

    private static bool TryMapBestParams(JsonElement row, string cls, string type, out BestParamsDto? dto)
    {
        dto = null;

        cls = (cls ?? "global").Trim().ToLowerInvariant();
        type = (type ?? "any").Trim().ToLowerInvariant();

        // ratings[cls]
        decimal? rating = null;
        if (row.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Object)
        {
            if (ratings.TryGetProperty(cls, out var rEl))
            {
                if (TryGetDecimal(rEl, out var rv))
                    rating = rv;
            }
        }

        // hard_soft_share[cls] -> hard/soft
        int hard = 0, soft = 0;
        if (row.TryGetProperty("hard_soft_share", out var hs) && hs.ValueKind == JsonValueKind.Object)
        {
            if (hs.TryGetProperty(cls, out var hsCls) && hsCls.ValueKind == JsonValueKind.Object)
            {
                if (hsCls.TryGetProperty("hard", out var hEl) && TryGetInt(hEl, out var hv)) hard = hv;
                if (hsCls.TryGetProperty("soft", out var sEl) && TryGetInt(sEl, out var sv)) soft = sv;
            }
        }

        var total = hard + soft;

        var hsLabel = type switch
        {
            "soft" => "S",
            "hard" => "H",
            _ => "A"
        };

        dto = BestParamsMapper.MapToSnapshot(
            bestParamsRow: row,
            clsRequested: cls,
            clsMapped: cls,
            rating: rating,
            total: total,
            hard: hard,
            soft: soft,
            hs: hsLabel);

        return true;
    }

    private static bool TryGetDecimal(JsonElement el, out decimal v)
    {
        v = 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out v);

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement el, out int v)
    {
        v = 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out v);

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s) &&
                int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return true;
        }

        return false;
    }
}
