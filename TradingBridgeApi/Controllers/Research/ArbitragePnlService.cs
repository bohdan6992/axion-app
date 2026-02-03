using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBridgeApi.Services.Tape;
using TradingBridgeApi.Services.Tape.Models;

namespace TradingBridgeApi.Services.Research
{
    public sealed class ArbitragePnlService
    {
        public record Query
        {
            public string DateNy { get; init; } = string.Empty;

            // time filter
            public int? MinuteFrom { get; init; }     // inclusive
            public int? MinuteTo { get; init; }       // inclusive

            public int HorizonMin { get; init; } = 1;
            public int CooldownMin { get; init; } = 0;

            // entry filters (CANON: % in percent units)
            public double? MinZapPct { get; init; }
            public double? MinSigmaZap { get; init; }

            // "long" | "short" | "both" | null
            public string? Dir { get; init; }

            public string[]? Tickers { get; init; }
            public int Limit { get; init; } = 500;

            // optional: sort by "pnl"|"ret"|"zap"|"time" (default time)
            public string? Sort { get; init; }
        }

        public record ArbOpportunityDto
        {
            public string Ticker { get; init; } = string.Empty;
            public string Dir { get; init; } = string.Empty;

            public string EntryTimeNy { get; init; } = string.Empty;
            public string ExitTimeNy { get; init; } = string.Empty;

            public int EntryMinuteIdx { get; init; }
            public int ExitMinuteIdx { get; init; }

            public double? ZapPctEntry { get; init; }
            public double? SigmaZapEntry { get; init; }

            public double? EntryBid { get; init; }
            public double? EntryAsk { get; init; }

            public double? ExitBid { get; init; }
            public double? ExitAsk { get; init; }

            public double? RetPct { get; init; }
            public double? TierUsd { get; init; }
            public double? PnlUsd { get; init; }
        }

        private readonly TapeQueryService _tape;
        public ArbitragePnlService(TapeQueryService tape) { _tape = tape; }

        public async Task<IReadOnlyList<ArbOpportunityDto>> QueryAsync(Query q, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(q.DateNy))
                return Array.Empty<ArbOpportunityDto>();

            // normalize (DO NOT mutate init-only props)
            var horizonMin = q.HorizonMin <= 0 ? 1 : q.HorizonMin;
            var cooldownMin = q.CooldownMin < 0 ? 0 : q.CooldownMin;

            var wantShort = IsDirEnabled(q.Dir, "short");
            var wantLong = IsDirEnabled(q.Dir, "long");

            if (!wantShort && !wantLong)
                return Array.Empty<ArbOpportunityDto>();

            // 1) pull tape rows (date + time window + tickers)
            var req = new TapeQueryRequest
            {
                DateNy = q.DateNy.Trim(),
                MinuteFrom = q.MinuteFrom,
                MinuteTo = q.MinuteTo,
                Tickers = q.Tickers,
                Limit = 0, // 0 => no cap at tape-level (we cap after PnL)
                // NOTE: do NOT apply zap/sigma filters at tape-level,
                // because they are direction-specific (short vs long).
                MinZapPct = null,
                MinSigmaZap = null
            };

            var rows = await _tape.QueryAsync(req, ct);
            if (rows.Count == 0)
                return Array.Empty<ArbOpportunityDto>();

            // 2) group by ticker, build minute index map
            var byTicker = rows
                .GroupBy(r => r.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.MinuteIdx).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var opportunities = new List<ArbOpportunityDto>(capacity: Math.Min(q.Limit, 10_000));

            foreach (var kv in byTicker)
            {
                ct.ThrowIfCancellationRequested();

                var ticker = kv.Key;
                var list = kv.Value;

                // minuteIdx -> row for O(1) exit lookup
                var map = new Dictionary<int, TapeRowV1>(capacity: list.Count);
                foreach (var r in list)
                    map[r.MinuteIdx] = r;

                int lastAccepted = int.MinValue;

                foreach (var entry in list)
                {
                    // cooldown
                    if (cooldownMin > 0 && entry.MinuteIdx - lastAccepted < cooldownMin)
                        continue;

                    var exitMinute = entry.MinuteIdx + horizonMin;
                    if (!map.TryGetValue(exitMinute, out var exit))
                        continue;

                    // try SHORT first, then LONG (you can change priority later)
                    if (wantShort && IsShortEntryOk(entry, q.MinZapPct, q.MinSigmaZap))
                    {
                        var opp = BuildShort(ticker, entry, exit);
                        if (opp != null)
                        {
                            opportunities.Add(opp);
                            lastAccepted = entry.MinuteIdx;
                        }
                    }
                    else if (wantLong && IsLongEntryOk(entry, q.MinZapPct, q.MinSigmaZap))
                    {
                        var opp = BuildLong(ticker, entry, exit);
                        if (opp != null)
                        {
                            opportunities.Add(opp);
                            lastAccepted = entry.MinuteIdx;
                        }
                    }

                    if (q.Limit > 0 && opportunities.Count >= q.Limit)
                        break;
                }

                if (q.Limit > 0 && opportunities.Count >= q.Limit)
                    break;
            }

            // 3) sorting (optional)
            opportunities = ApplySort(opportunities, q.Sort);

            // 4) final cap
            if (q.Limit > 0 && opportunities.Count > q.Limit)
                opportunities = opportunities.Take(q.Limit).ToList();

            return opportunities;
        }

        private static bool IsDirEnabled(string? dir, string side)
        {
            if (string.IsNullOrWhiteSpace(dir)) return true;
            var d = dir.Trim().ToLowerInvariant();
            if (d == "both") return true;
            return d == side;
        }

        // CANON:
        // SHORT: zapPctS >= minZapPct, sigmaZapS >= minSigmaZap (if provided)
        private static bool IsShortEntryOk(TapeRowV1 r, double? minZapPct, double? minSigmaZap)
        {
            if (!r.ZapPctS.HasValue) return false;

            if (minZapPct.HasValue && r.ZapPctS.Value < minZapPct.Value)
                return false;

            if (minSigmaZap.HasValue)
            {
                if (!r.SigmaZapS.HasValue) return false;
                if (r.SigmaZapS.Value < minSigmaZap.Value) return false;
            }

            return true;
        }

        // CANON:
        // LONG: zapPctL <= -minZapPct, sigmaZapL <= -minSigmaZap (if provided)
        private static bool IsLongEntryOk(TapeRowV1 r, double? minZapPct, double? minSigmaZap)
        {
            if (!r.ZapPctL.HasValue) return false;

            if (minZapPct.HasValue && r.ZapPctL.Value > -minZapPct.Value)
                return false;

            if (minSigmaZap.HasValue)
            {
                if (!r.SigmaZapL.HasValue) return false;
                if (r.SigmaZapL.Value > -minSigmaZap.Value) return false;
            }

            return true;
        }

        private static ArbOpportunityDto? BuildLong(string ticker, TapeRowV1 entry, TapeRowV1 exit)
        {
            // LONG: entry=ask_A, exit=bid_B
            if (!entry.Ask.HasValue || !exit.Bid.HasValue) return null;

            var entryPx = entry.Ask.Value;
            var exitPx = exit.Bid.Value;
            if (entryPx <= 0) return null;

            var retPct = ((exitPx - entryPx) / entryPx) * 100.0;

            var tierUsd = entry.TierBp; // for now; later move to static.parquet TierUsd
            double? pnlUsd = tierUsd.HasValue ? (retPct / 100.0) * tierUsd.Value : null;

            return new ArbOpportunityDto
            {
                Ticker = ticker,
                Dir = "long",

                EntryTimeNy = entry.MinuteNy,
                ExitTimeNy = exit.MinuteNy,

                EntryMinuteIdx = entry.MinuteIdx,
                ExitMinuteIdx = exit.MinuteIdx,

                ZapPctEntry = entry.ZapPctL,
                SigmaZapEntry = entry.SigmaZapL,

                EntryBid = entry.Bid,
                EntryAsk = entry.Ask,

                ExitBid = exit.Bid,
                ExitAsk = exit.Ask,

                RetPct = Math.Round(retPct, 6),
                TierUsd = tierUsd,
                PnlUsd = pnlUsd
            };
        }

        private static ArbOpportunityDto? BuildShort(string ticker, TapeRowV1 entry, TapeRowV1 exit)
        {
            // SHORT: entry=bid_A, exit=ask_B
            if (!entry.Bid.HasValue || !exit.Ask.HasValue) return null;

            var entryPx = entry.Bid.Value;
            var exitPx = exit.Ask.Value;
            if (entryPx <= 0) return null;

            // CANON SHORT return:
            // retPct = (bid_A - ask_B) / bid_A * 100
            var retPct = ((entryPx - exitPx) / entryPx) * 100.0;

            var tierUsd = entry.TierBp; // for now; later move to static.parquet TierUsd
            double? pnlUsd = tierUsd.HasValue ? (retPct / 100.0) * tierUsd.Value : null;

            return new ArbOpportunityDto
            {
                Ticker = ticker,
                Dir = "short",

                EntryTimeNy = entry.MinuteNy,
                ExitTimeNy = exit.MinuteNy,

                EntryMinuteIdx = entry.MinuteIdx,
                ExitMinuteIdx = exit.MinuteIdx,

                ZapPctEntry = entry.ZapPctS,
                SigmaZapEntry = entry.SigmaZapS,

                EntryBid = entry.Bid,
                EntryAsk = entry.Ask,

                ExitBid = exit.Bid,
                ExitAsk = exit.Ask,

                RetPct = Math.Round(retPct, 6),
                TierUsd = tierUsd,
                PnlUsd = pnlUsd
            };
        }

        private static List<ArbOpportunityDto> ApplySort(List<ArbOpportunityDto> items, string? sort)
        {
            if (items.Count <= 1) return items;

            var s = (sort ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "pnl" => items.OrderByDescending(x => x.PnlUsd ?? double.MinValue).ToList(),
                "ret" => items.OrderByDescending(x => x.RetPct ?? double.MinValue).ToList(),
                "zap" => items.OrderByDescending(x => Math.Abs(x.ZapPctEntry ?? 0)).ToList(),
                "time" or "" => items.OrderBy(x => x.EntryMinuteIdx).ThenBy(x => x.Ticker).ToList(),
                _ => items.OrderBy(x => x.EntryMinuteIdx).ThenBy(x => x.Ticker).ToList()
            };
        }
    }
}
