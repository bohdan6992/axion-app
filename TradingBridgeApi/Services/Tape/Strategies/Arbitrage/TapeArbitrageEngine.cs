using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBridgeApi.Services.Tape;
using TradingBridgeApi.Services.Tape.Models;
using TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models;
using TradingBridgeApi.Services.Strategy.Arbitrage;

namespace TradingBridgeApi.Services.Tape.Strategies.Arbitrage
{
    public sealed class TapeArbitrageEngine
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly TapeQueryService _tape;
        private readonly IArbitrageFilesService _arbFiles;
        private readonly ILogger<TapeArbitrageEngine> _log;

        public TapeArbitrageEngine(
            TapeQueryService tape,
            IArbitrageFilesService arbFiles,
            ILogger<TapeArbitrageEngine> log)
        {
            _tape = tape;
            _arbFiles = arbFiles;
            _log = log;
        }

        public async Task<(Dictionary<string, TapeArbState> states, List<TapeArbClosed> closed, int lastMinute)> BuildDayAsync(
            TapeArbParams p,
            CancellationToken ct)
        {
            var dateNy = (p.DateNy ?? "").Trim();
            if (dateNy.Length == 0)
                throw new ArgumentException("DateNy is required (yyyy-MM-dd)");

            // Prebuild ticker filter (fast O(1) contains)
            HashSet<string>? tickersSet = null;
            if (p.Tickers is { Length: > 0 })
            {
                tickersSet = new HashSet<string>(
                    p.Tickers.Where(x => !string.IsNullOrWhiteSpace(x))
                             .Select(x => x.Trim().ToUpperInvariant()),
                    StringComparer.OrdinalIgnoreCase);
            }

            // read static best_params for gating (minRate/minTotal) + beta/sigma already in tape rows but we also need rating/total
            var staticGate = await LoadStaticGateAsync(p, ct);

            var states = new Dictionary<string, TapeArbState>(StringComparer.OrdinalIgnoreCase);
            var closed = new List<TapeArbClosed>();

            // process minutes 0..1199 (writer writes only 0..1199)
            var lastMinute = -1;

            for (int minuteIdx = 0; minuteIdx <= 1199; minuteIdx++)
            {
                ct.ThrowIfCancellationRequested();

                // If parquet file doesn't exist - skip quickly (no exceptions)
                if (!_tape.HasMinute(dateNy, minuteIdx))
                    continue;

                IReadOnlyList<TapeRowV1> rows;
                try
                {
                    rows = await _tape.QueryAsync(new TapeQueryRequest
                    {
                        DateNy = dateNy,
                        MinuteFrom = minuteIdx,
                        MinuteTo = minuteIdx,
                        // If tickers filter provided, pass it into query to avoid reading/unnecessary filtering later
                        Tickers = tickersSet == null ? null : tickersSet.ToArray(),
                        Limit = 0
                    }, ct);
                }
                catch
                {
                    // minute may be partially written / IO error => skip
                    continue;
                }

                if (rows == null || rows.Count == 0) continue;

                lastMinute = minuteIdx;

                foreach (var r in rows)
                {
                    if (r?.Ticker == null) continue;

                    // optional tickers filter (extra safety if query didn't filter)
                    if (tickersSet != null && !tickersSet.Contains(r.Ticker))
                        continue;

                    // gating by static rating/total
                    if (!PassesStaticGate(staticGate, r.Ticker))
                        continue;

                    // choose side & dev according to tape candidates
                    if (!TryPickDev(p.Metric, r, out var side, out var dev))
                        continue;

                    var absDev = Math.Abs(dev);

                    // if not in dict create
                    if (!states.TryGetValue(r.Ticker, out var st))
                    {
                        st = new TapeArbState
                        {
                            IsActive = false,
                            BenchTicker = r.BenchTicker ?? "",
                            LastMinuteIdx = minuteIdx,
                            LastDev = dev,
                            Side = side,

                            TierBp = r.TierBp,
                            Beta = r.Beta,
                            HedgeNotional = (r.TierBp.HasValue && r.Beta.HasValue) ? r.TierBp.Value * r.Beta.Value : null,

                            // нові поля класів (дефолти)
                            StartClass = TapeArbClass.GLOB,
                            StartClassEndMinuteIdx = 0,
                            MinAbsDevInClass = 0,
                            MinAbsDevMinuteIdxInClass = 0,
                            EndDevAtClassEnd = null
                        };

                        // attach rating/total if present
                        if (staticGate.TryGetValue(r.Ticker, out var g))
                        {
                            st.Rating = g.Rating;
                            st.Total = g.Total;
                        }

                        states[r.Ticker] = st;
                    }

                    // always update bench ticker if present
                    if (!string.IsNullOrWhiteSpace(r.BenchTicker))
                        st.BenchTicker = r.BenchTicker!;

                    st.LastMinuteIdx = minuteIdx;
                    st.LastDev = dev;
                    st.Side = side;

                    // start logic
                    if (!st.IsActive)
                    {
                        if (absDev >= p.StartAbs)
                        {
                            st.IsActive = true;
                            st.StartMinuteIdx = minuteIdx;
                            st.StartDev = dev;

                            st.PeakMinuteIdx = minuteIdx;
                            st.PeakDevAbs = absDev;
                            st.PeakDev = dev;

                            // ---- ІНІЦІАЛІЗАЦІЯ КЛАСУ / MIN(|dev|) ДЛЯ ЕПІЗОДУ ----
                            var startClass = TapeArbClasses.ClassByStartMinute(minuteIdx);
                            st.StartClass = startClass;

                            var wnd = TapeArbClasses.Window(startClass);
                            st.StartClassEndMinuteIdx = wnd.To;

                            st.MinAbsDevInClass = absDev;
                            st.MinAbsDevMinuteIdxInClass = minuteIdx;
                            st.EndDevAtClassEnd = null;

                            // entry pct for PnL: per your rule
                            // LONG: entry AskPct, SHORT: entry BidPct
                            st.StockEntryPct = (side == TapeArbSide.Long) ? r.AskPct : r.BidPct;

                            // hedge entry pct: opposite side on bench
                            // stock LONG => hedge SHORT bench => entry BenchBidPct
                            // stock SHORT => hedge LONG bench => entry BenchAskPct
                            st.BenchEntryPct = (side == TapeArbSide.Long) ? r.BenchBidPct : r.BenchAskPct;

                            st.TierBp = r.TierBp ?? st.TierBp;
                            st.Beta = r.Beta ?? st.Beta;
                            st.HedgeNotional = (st.TierBp.HasValue && st.Beta.HasValue)
                                ? st.TierBp.Value * st.Beta.Value
                                : st.HedgeNotional;
                        }

                        continue;
                    }

                    // active: update peak
                    if (absDev > st.PeakDevAbs)
                    {
                        st.PeakDevAbs = absDev;
                        st.PeakDev = dev;
                        st.PeakMinuteIdx = minuteIdx;
                    }

                    // active: update min |dev| у межах стартового класу
                    if (minuteIdx <= st.StartClassEndMinuteIdx)
                    {
                        if (st.MinAbsDevInClass <= 0 || absDev < st.MinAbsDevInClass)
                        {
                            st.MinAbsDevInClass = absDev;
                            st.MinAbsDevMinuteIdxInClass = minuteIdx;
                        }
                    }

                    // active: зафіксувати dev на кінці класу для INTRA/POST/NIGHT
                    if (minuteIdx == st.StartClassEndMinuteIdx)
                    {
                        if (st.StartClass == TapeArbClass.INTRA ||
                            st.StartClass == TapeArbClass.POST ||
                            st.StartClass == TapeArbClass.NIGHT)
                        {
                            st.EndDevAtClassEnd = dev;
                        }
                    }

                    // end logic
                    if (absDev <= p.EndAbs)
                    {
                        // exit pct for PnL
                        var stockExitPct = (side == TapeArbSide.Long) ? r.BidPct : r.AskPct;

                        // hedge exit pct: opposite side on bench
                        // stock LONG => hedge SHORT => exit BenchAskPct
                        // stock SHORT => hedge LONG => exit BenchBidPct
                        var benchExitPct = (side == TapeArbSide.Long) ? r.BenchAskPct : r.BenchBidPct;

                        var closedOne = BuildClosed(st, r, dateNy, minuteIdx, dev, stockExitPct, benchExitPct);

                        closed.Add(closedOne);

                        // reset state
                        st.IsActive = false;
                        st.StartMinuteIdx = 0;
                        st.StartDev = 0;
                        st.PeakMinuteIdx = 0;
                        st.PeakDevAbs = 0;
                        st.PeakDev = 0;
                        st.StockEntryPct = null;
                        st.BenchEntryPct = null;

                        // класові поля теж можна обнулити (не обов'язково, але акуратно)
                        st.StartClass = TapeArbClass.GLOB;
                        st.StartClassEndMinuteIdx = 0;
                        st.MinAbsDevInClass = 0;
                        st.MinAbsDevMinuteIdxInClass = 0;
                        st.EndDevAtClassEnd = null;
                    }
                }
            }

            return (states, closed, lastMinute);
        }

        private sealed class StaticGate
        {
            public double? Rating { get; set; }
            public int? Total { get; set; }
        }

        private async Task<Dictionary<string, StaticGate>> LoadStaticGateAsync(TapeArbParams p, CancellationToken ct)
        {
            // only if filters set; else still useful to include rating/total into output
            try
            {
                // We can’t know full universe here; request “ALL known” is expensive.
                // But best_map supports requesting tickers; we’ll build a soft map:
                // - If user passes explicit tickers -> request those
                // - Else return empty map (no gating), and controller can pass tickers for current day later.
                if (p.Tickers == null || p.Tickers.Length == 0)
                    return new Dictionary<string, StaticGate>(StringComparer.OrdinalIgnoreCase);

                var bestMap = await _arbFiles.GetBestParamsForTickersAsync(p.Tickers.ToList(), ct);

                var res = new Dictionary<string, StaticGate>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in bestMap)
                {
                    var t = (kv.Key ?? "").Trim();
                    if (t.Length == 0) continue;

                    var g = new StaticGate();
                    TryReadRatingTotal(kv.Value, out var rating, out var total);
                    g.Rating = rating;
                    g.Total = total;

                    res[t] = g;
                }
                return res;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TapeArb] static gate load failed (ignored)");
                return new Dictionary<string, StaticGate>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool PassesStaticGate(Dictionary<string, StaticGate> gate, string ticker)
        {
            // if gate is empty -> no gating
            if (gate.Count == 0) return true;

            if (!gate.TryGetValue(ticker, out var g)) return false; // strict if we are gating
            if (g == null) return false;

            return true;
        }

        private static void TryReadRatingTotal(JsonElement row, out double? rating, out int? total)
        {
            rating = null;
            total = null;

            try
            {
                // common shapes:
                // 1) { best: { rating, total } }
                // 2) { Best: { Rating, Total } }
                // 3) { rating, total }
                JsonElement obj = row;

                if (obj.ValueKind != JsonValueKind.Object) return;

                if (obj.TryGetProperty("best", out var best) && best.ValueKind == JsonValueKind.Object)
                    obj = best;
                else if (obj.TryGetProperty("Best", out var Best) && Best.ValueKind == JsonValueKind.Object)
                    obj = Best;

                if (obj.TryGetProperty("rating", out var r1) && r1.ValueKind == JsonValueKind.Number && r1.TryGetDouble(out var rd1))
                    rating = rd1;
                else if (obj.TryGetProperty("Rating", out var r2) && r2.ValueKind == JsonValueKind.Number && r2.TryGetDouble(out var rd2))
                    rating = rd2;

                if (obj.TryGetProperty("total", out var t1) && t1.ValueKind == JsonValueKind.Number && t1.TryGetInt32(out var ti1))
                    total = ti1;
                else if (obj.TryGetProperty("Total", out var t2) && t2.ValueKind == JsonValueKind.Number && t2.TryGetInt32(out var ti2))
                    total = ti2;
            }
            catch
            {
                // ignore
            }
        }

        private static bool TryPickDev(TapeArbMetric metric, TapeRowV1 r, out TapeArbSide side, out double dev)
        {
            side = TapeArbSide.Long;
            dev = 0;

            // side: based on candidates
            // - LongCandidate => use L fields
            // - ShortCandidate => use S fields
            // If both null/false => no signal
            var isLong = r.LongCandidate == true;
            var isShort = r.ShortCandidate == true;

            if (!isLong && !isShort)
                return false;

            // If both true (rare), pick bigger abs dev by metric.
            if (metric == TapeArbMetric.SigmaZap)
            {
                var s = r.SigmaZapS;
                var l = r.SigmaZapL;

                if (isLong && isShort && s.HasValue && l.HasValue)
                {
                    if (Math.Abs(l.Value) >= Math.Abs(s.Value)) { side = TapeArbSide.Long; dev = l.Value; return true; }
                    side = TapeArbSide.Short; dev = s.Value; return true;
                }

                if (isLong && l.HasValue) { side = TapeArbSide.Long; dev = l.Value; return true; }
                if (isShort && s.HasValue) { side = TapeArbSide.Short; dev = s.Value; return true; }

                return false;
            }
            else
            {
                var s = r.ZapPctS;
                var l = r.ZapPctL;

                if (isLong && isShort && s.HasValue && l.HasValue)
                {
                    if (Math.Abs(l.Value) >= Math.Abs(s.Value)) { side = TapeArbSide.Long; dev = l.Value; return true; }
                    side = TapeArbSide.Short; dev = s.Value; return true;
                }

                if (isLong && l.HasValue) { side = TapeArbSide.Long; dev = l.Value; return true; }
                if (isShort && s.HasValue) { side = TapeArbSide.Short; dev = s.Value; return true; }

                return false;
            }
        }

        private static TapeArbClosed BuildClosed(
            TapeArbState st,
            TapeRowV1 r,
            string dateNy,
            int endMinuteIdx,
            double endDev,
            double? stockExitPct,
            double? benchExitPct)
        {
            // PnL:
            // stock pnl = (exit - entry) * notional / 100 for LONG
            // for SHORT: pnl = (entry - exit) * notional / 100
            double? stockPnlUsd = null;
            if (st.TierBp.HasValue && st.StockEntryPct.HasValue && stockExitPct.HasValue)
            {
                var entry = st.StockEntryPct.Value;
                var exit = stockExitPct.Value;

                var dPct = (st.Side == TapeArbSide.Long) ? (exit - entry) : (entry - exit);
                stockPnlUsd = dPct / 100.0 * st.TierBp.Value;
            }

            double? hedgePnlUsd = null;
            if (st.HedgeNotional.HasValue && st.BenchEntryPct.HasValue && benchExitPct.HasValue)
            {
                var entry = st.BenchEntryPct.Value;
                var exit = benchExitPct.Value;

                // hedge is opposite:
                // stock LONG => hedge SHORT => pnl = (entry - exit)
                // stock SHORT => hedge LONG => pnl = (exit - entry)
                var dPct = (st.Side == TapeArbSide.Long) ? (entry - exit) : (exit - entry);
                hedgePnlUsd = dPct / 100.0 * st.HedgeNotional.Value;
            }

            var totalUsd = (stockPnlUsd ?? 0) + (hedgePnlUsd ?? 0);

            // ---- КЛАСОВІ ФЛАГИ / НОРМАЛІЗАЦІЯ ----
            var startClass = st.StartClass;
            var startWnd = TapeArbClasses.Window(startClass);

            var normInSameClass = endMinuteIdx >= startWnd.From && endMinuteIdx <= startWnd.To;

            var normInNextClass = false;
            var nextClass = TapeArbClasses.Next(startClass);
            if (nextClass.HasValue)
            {
                var nextWnd = TapeArbClasses.Window(nextClass.Value);
                normInNextClass = endMinuteIdx >= nextWnd.From && endMinuteIdx <= nextWnd.To;
            }

            bool printNorm = false;
            bool openNorm = false;

            if (startClass == TapeArbClass.BLUE || startClass == TapeArbClass.ARK)
            {
                printNorm = TapeArbClasses.IsInPrintWindow(endMinuteIdx);
                openNorm = TapeArbClasses.IsInOpenWindow(endMinuteIdx);
            }

            return new TapeArbClosed
            {
                Status = TapeArbStatus.Closed,
                DateNy = dateNy,

                Ticker = r.Ticker ?? "",
                BenchTicker = st.BenchTicker ?? "",

                Side = st.Side,

                StartMinuteIdx = st.StartMinuteIdx,
                PeakMinuteIdx = st.PeakMinuteIdx,
                EndMinuteIdx = endMinuteIdx,

                StartDev = st.StartDev,
                PeakDev = st.PeakDev,
                EndDev = endDev,

                Rating = st.Rating,
                Total = st.Total,

                TierBp = st.TierBp,
                Beta = st.Beta,
                HedgeNotional = st.HedgeNotional,

                StockEntryPct = st.StockEntryPct,
                StockExitPct = stockExitPct,

                BenchEntryPct = st.BenchEntryPct,
                BenchExitPct = benchExitPct,

                StockPnlUsd = stockPnlUsd,
                HedgePnlUsd = hedgePnlUsd,
                TotalPnlUsd = totalUsd,

                // нові поля
                StartClass = startClass,
                NormInSameClass = normInSameClass,
                NormInNextClass = normInNextClass,
                PrintNorm = printNorm,
                OpenNorm = openNorm,
                MinAbsDevInClass = st.MinAbsDevInClass,
                MinAbsDevMinuteIdxInClass = st.MinAbsDevMinuteIdxInClass,
                EndDevAtClassEnd = st.EndDevAtClassEnd
            };
        }
    }
}
