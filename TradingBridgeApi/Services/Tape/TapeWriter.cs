using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Parquet;                       // CompressionMethod
using Parquet.Serialization;          // ParquetSerializer
using TradingBridgeApi.Services.Live; // UniverseService
using TradingBridgeApi.Services.Tape.Models;
using TradingBridgeApi.Services.Strategy.Arbitrage; // ✅ IArbitrageFilesService
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeWriter : ITapeWriter
    {
        private readonly LiveSnapshotService _live;
        private readonly UniverseService _universe;
        private readonly IArbitrageFilesService _arbFiles; // ✅ same static source as arbitrage strategy
        private readonly TapeFilePaths _paths;
        private readonly TapeRetentionService _retention;
        private readonly ILogger<TapeWriter> _log;

        private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public TapeWriter(
            LiveSnapshotService live,
            UniverseService universe,
            IArbitrageFilesService arbFiles, // ✅ injected
            TapeFilePaths paths,
            TapeRetentionService retention,
            ILogger<TapeWriter> log)
        {
            _live = live;
            _universe = universe;
            _arbFiles = arbFiles;
            _paths = paths;
            _retention = retention;
            _log = log;
        }

        public async Task<TapeWriteResult> WriteMinuteAsync(DateTime utcNow, CancellationToken ct = default)
        {
            var utcMinute = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);
            var nyTime = TimeZoneInfo.ConvertTimeFromUtc(utcMinute, NyTz);

            // Canon: write 00:00..19:59 NY
            if (!IsNyWriteableMinute(nyTime))
            {
                return new TapeWriteResult(
                    TapeWriteStatus.Skipped,
                    "NY minute out of write window (00:00..19:59)",
                    utcMinute,
                    nyTime.ToString("yyyy-MM-dd", Inv),
                    nyTime.Hour * 60 + nyTime.Minute,
                    0);
            }

            var dateNy = nyTime.ToString("yyyy-MM-dd", Inv);
            var minuteNy = nyTime.ToString("HH:mm", Inv);
            var minuteIdx = nyTime.Hour * 60 + nyTime.Minute; // 0..1199
            var band = GetBand(nyTime);

            // Retention
            try { await _retention.EnforceRetentionAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "[TapeWriter] retention failed (ignored)"); }

            // Idempotency
            var minuteDir = _paths.MinuteDir(dateNy, minuteIdx);
            var filePath = _paths.MinuteFile(dateNy, minuteIdx);

            if (File.Exists(filePath))
            {
                return new TapeWriteResult(
                    TapeWriteStatus.Skipped,
                    "Minute already exists (idempotent skip)",
                    utcMinute,
                    dateNy,
                    minuteIdx,
                    0);
            }

            Directory.CreateDirectory(minuteDir);

            // ✅ Universe: ticker -> bench (same as arbitrage strategy uses)
            Dictionary<string, string> benchByTicker;
            try
            {
                benchByTicker = await _universe.LoadUniverseWithBenchAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[TapeWriter] failed to load universe with bench");
                return new TapeWriteResult(TapeWriteStatus.Failed, "Universe load failed", utcMinute, dateNy, minuteIdx, 0);
            }

            if (benchByTicker.Count == 0)
            {
                _log.LogWarning("[TapeWriter] universe empty (no tickers) for {dateNy} {minuteNy}", dateNy, minuteNy);
                return new TapeWriteResult(TapeWriteStatus.Skipped, "Universe empty", utcMinute, dateNy, minuteIdx, 0);
            }

            var universeTickers = benchByTicker.Keys
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var benchTickers = benchByTicker.Values
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ✅ Snapshot FULL for (universe + benches) in one call
            var requestTickers = universeTickers
                .Concat(benchTickers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<LiveSnapshotItemDto> snapItems;
            try
            {
                var (_, got) = await _live.GetSnapshotForTickersAsync(requestTickers, fieldsCsv: "FULL", ct: ct);
                snapItems = got ?? new List<LiveSnapshotItemDto>();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[TapeWriter] snapshot failed for {dateNy} {minuteNy}", dateNy, minuteNy);
                return new TapeWriteResult(TapeWriteStatus.Failed, "Snapshot failed", utcMinute, dateNy, minuteIdx, 0);
            }

            if (snapItems.Count == 0)
            {
                _log.LogWarning("[TapeWriter] snapshot empty for {dateNy} {minuteNy}", dateNy, minuteNy);
                return new TapeWriteResult(TapeWriteStatus.Skipped, "Snapshot empty", utcMinute, dateNy, minuteIdx, 0);
            }

            var liveByTicker = snapItems
                .Where(x => x?.Ticker != null)
                .GroupBy(x => x!.Ticker!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // ✅ Static beta/sigma from SAME SOURCE as arbitrage strategy
            Dictionary<string, (double? Beta, double? Sigma)> staticMap;
            int betaNonNull = 0, sigmaNonNull = 0;
            try
            {
                var bestMap = await _arbFiles.GetBestParamsForTickersAsync(universeTickers, ct);
                staticMap = BuildBetaSigmaMap(bestMap, out betaNonNull, out sigmaNonNull);

                _log.LogInformation("[TapeWriter] loaded static beta/sigma rows={count} betaNonNull={betaN} sigmaNonNull={sigN}",
                    staticMap.Count, betaNonNull, sigmaNonNull);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TapeWriter] static beta/sigma load failed (tape continues without it)");
                staticMap = new Dictionary<string, (double? Beta, double? Sigma)>(StringComparer.OrdinalIgnoreCase);
            }

            // Map -> TapeRowV1 (only universe tickers; benches are used for enrich)
            var rows = new List<TapeRowV1>(universeTickers.Count);

            foreach (var ticker in universeTickers)
            {
                ct.ThrowIfCancellationRequested();

                if (!liveByTicker.TryGetValue(ticker, out var item) || item?.Fields == null)
                    continue;

                // ✅ Bench enrich in meta (same semantics as arbitrage handler)
                var meta = item.Fields;
                var bench = benchByTicker.TryGetValue(ticker, out var b) ? (b ?? "").Trim().ToUpperInvariant() : "";

                if (!string.IsNullOrWhiteSpace(bench))
                {
                    meta["Benchmark"] = bench;
                    meta["bench.ticker"] = bench;

                    if (liveByTicker.TryGetValue(bench, out var benchItem) && benchItem?.Fields != null)
                    {
                        var bm = benchItem.Fields;

                        // BidLstClsΔ%, AskLstClsΔ% -> injected as BenchBidLstClsΔ%, BenchAskLstClsΔ%
                        if (TryGetDoubleFromDict(bm, out var bbp, "BidLstClsΔ%"))
                            meta["BenchBidLstClsΔ%"] = bbp;
                        if (TryGetDoubleFromDict(bm, out var bap, "AskLstClsΔ%"))
                            meta["BenchAskLstClsΔ%"] = bap;

                        // optional back-compat
                        if (TryGetDoubleFromDict(bm, out var bBid, "Bid"))
                            meta["bench.Bid"] = bBid;
                        if (TryGetDoubleFromDict(bm, out var bAsk, "Ask"))
                            meta["bench.Ask"] = bAsk;
                        if (TryGetDoubleFromDict(bm, out var bCls, "LstCls", "YCls"))
                            meta["bench.LstCls"] = bCls;
                    }
                }

                try
                {
                    rows.Add(MapSnapshotToTapeRow(item, dateNy, minuteIdx, minuteNy, band, staticMap));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TapeWriter] mapping failed for {ticker}", ticker);
                }
            }

            if (rows.Count == 0)
            {
                return new TapeWriteResult(TapeWriteStatus.Failed, "No rows after mapping", utcMinute, dateNy, minuteIdx, 0);
            }

            // Write parquet atomically
            var tmp = Path.Combine(minuteDir, $"part.{Guid.NewGuid():N}.tmp.parquet");

            try
            {
                await WriteParquetAsync(tmp, rows, ct);
                File.Move(tmp, filePath, overwrite: true);

                _log.LogInformation("[TapeWriter] wrote {count} rows => {dateNy} {minuteNy} idx={idx}",
                    rows.Count, dateNy, minuteNy, minuteIdx);

                return new TapeWriteResult(TapeWriteStatus.Written, "OK", utcMinute, dateNy, minuteIdx, rows.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[TapeWriter] parquet write failed for {dateNy} {minuteNy}", dateNy, minuteNy);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }

                return new TapeWriteResult(TapeWriteStatus.Failed, "Parquet write failed", utcMinute, dateNy, minuteIdx, 0);
            }
        }

        // ✅ IMPORTANT: matches BestParamsMapper contract:
        // beta/sigma live under best_params.static.beta / best_params.static.sigma
        private static Dictionary<string, (double? Beta, double? Sigma)> BuildBetaSigmaMap(
            Dictionary<string, JsonElement> bestMap,
            out int betaNonNull,
            out int sigmaNonNull)
        {
            betaNonNull = 0;
            sigmaNonNull = 0;

            var map = new Dictionary<string, (double? Beta, double? Sigma)>(StringComparer.OrdinalIgnoreCase);

            static double? ReadNum(JsonElement obj, string prop)
            {
                if (!obj.TryGetProperty(prop, out var el)) return null;

                if (el.ValueKind == JsonValueKind.Number)
                {
                    if (el.TryGetDouble(out var d)) return d;
                    return null;
                }

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (double.TryParse(s, NumberStyles.Any, Inv, out var v)) return v;
                    return null;
                }

                return null;
            }

            foreach (var kv in bestMap)
            {
                var t = (kv.Key ?? "").Trim().ToUpperInvariant();
                if (t.Length == 0) continue;

                var row = kv.Value;

                double? beta = null;
                double? sigma = null;

                try
                {
                    if (row.ValueKind == JsonValueKind.Object)
                    {
                        // 1) canonical: static.beta/static.sigma
                        if (row.TryGetProperty("static", out var st) && st.ValueKind == JsonValueKind.Object)
                        {
                            beta = ReadNum(st, "beta");
                            sigma = ReadNum(st, "sigma");
                        }

                        // 2) fallback: top-level beta/sigma (older formats)
                        beta ??= ReadNum(row, "beta");
                        sigma ??= ReadNum(row, "sigma");
                    }
                }
                catch { /* ignore */ }

                if (beta.HasValue) betaNonNull++;
                if (sigma.HasValue) sigmaNonNull++;

                map[t] = (beta, sigma);
            }

            return map;
        }

        private static bool TryGetDoubleFromDict(Dictionary<string, object?> dict, out double value, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (dict.TryGetValue(k, out var o) && o != null)
                {
                    if (o is double d) { value = d; return true; }
                    if (o is float f) { value = f; return true; }
                    if (o is decimal dec) { value = (double)dec; return true; }
                    if (o is int i) { value = i; return true; }
                    if (o is long l) { value = l; return true; }
                    if (double.TryParse(o.ToString(), NumberStyles.Any, Inv, out var v)) { value = v; return true; }
                }
            }
            value = default;
            return false;
        }

        private static bool IsNyWriteableMinute(DateTime ny)
            => ny.Hour >= 0 && ny.Hour <= 19;

        private static bool IsBoundaryMinute(DateTime ny)
        {
            if (ny.Hour == 0 && ny.Minute == 0) return true;
            if (ny.Hour == 4 && ny.Minute == 0) return true;
            if (ny.Hour == 7 && ny.Minute == 0) return true;
            if (ny.Hour == 9 && ny.Minute == 30) return true;
            if (ny.Hour == 12 && ny.Minute == 0) return true;
            if (ny.Hour == 16 && ny.Minute == 0) return true;
            return false;
        }

        private static string GetBand(DateTime ny)
        {
            if (IsBoundaryMinute(ny)) return "SPECIAL";

            var idx = ny.Hour * 60 + ny.Minute;

            if (idx >= 1 && idx <= 239) return "BLOOTION";
            if (idx >= 241 && idx <= 419) return "EARLY_PRE";
            if (idx >= 421 && idx <= 569) return "LATE_PRE";
            if (idx >= 571 && idx <= 719) return "EARLY_INTRA";
            if (idx >= 721 && idx <= 959) return "LATE_INTRA";
            if (idx >= 961 && idx <= 1199) return "POST";

            return "SPECIAL";
        }

        private static TapeRowV1 MapSnapshotToTapeRow(
            LiveSnapshotItemDto item,
            string dateNy,
            int minuteIdx,
            string minuteNy,
            string band,
            Dictionary<string, (double? Beta, double? Sigma)> staticMap)
        {
            var meta = item.Fields ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            double? TryGetDouble(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (meta.TryGetValue(k, out var o) && o != null)
                    {
                        if (o is double d) return d;
                        if (o is float f) return f;
                        if (o is decimal dec) return (double)dec;
                        if (o is int i) return i;
                        if (o is long l) return l;
                        if (double.TryParse(o.ToString(), NumberStyles.Any, Inv, out var v)) return v;
                    }
                }
                return null;
            }

            bool? TryGetBool(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (meta.TryGetValue(k, out var o) && o != null)
                    {
                        var s = o.ToString()!.Trim();
                        if (string.Equals(s, "YES", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals(s, "NO", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase)) return false;
                        if (bool.TryParse(s, out var b)) return b;
                    }
                }
                return null;
            }

            int? TryGetInt(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (meta.TryGetValue(k, out var o) && o != null)
                    {
                        if (o is int ii) return ii;
                        if (o is long ll && ll >= int.MinValue && ll <= int.MaxValue) return (int)ll;
                        if (int.TryParse(o.ToString(), NumberStyles.Any, Inv, out var v)) return v;
                    }
                }
                return null;
            }

            string? benchTicker =
                meta.TryGetValue("bench.ticker", out var bt0) ? bt0?.ToString() :
                meta.TryGetValue("Benchmark", out var bt1) ? bt1?.ToString() : null;

                var bid = TryGetDouble("Bid");
                var ask = TryGetDouble("Ask");
                var spread = TryGetDouble("Spread");

                // TRAP closes
                var lstClose = TryGetDouble("LstCls", "YCls"); // як і раніше
                var yCls = TryGetDouble("YCls");
                var lstCls = TryGetDouble("LstCls");

                // TRAP day deltas
                var gap = TryGetDouble("Gap");
                var gapPct = TryGetDouble("Gap%");
                var clsToClsPct = TryGetDouble("ClsToCls%");

                var bidPct = TryGetDouble("BidLstClsΔ%");
                var askPct = TryGetDouble("AskLstClsΔ%");


            var benchBid = TryGetDouble("BenchBid", "bench.Bid");
            var benchAsk = TryGetDouble("BenchAsk", "bench.Ask");
            var benchLst = TryGetDouble("BenchLstCls", "bench.LstCls");

            var benchBidPct = TryGetDouble("BenchBidLstClsΔ%");
            var benchAskPct = TryGetDouble("BenchAskLstClsΔ%");

            double? beta = TryGetDouble("beta", "Beta");
            double? sigma = TryGetDouble("sigma", "Sigma");

            if ((!beta.HasValue || !sigma.HasValue) && staticMap.TryGetValue(item.Ticker, out var st))
            {
                beta ??= st.Beta;
                sigma ??= st.Sigma;
            }

            // ======================
            // OLD CANON (signals) — all in PCT space
            // ======================
            // STruePricePct = BenchAskPct * Beta
            // LTruePricePct = BenchBidPct * Beta
            // ZapPctS = BidPct - STruePricePct
            // ZapPctL = AskPct - LTruePricePct
            // SigmaZapS = ZapPctS / Sigma
            // SigmaZapL = ZapPctL / Sigma
            // ShortCandidate = ZapPctS > 0
            // LongCandidate  = ZapPctL < 0
            double? truePriceS = null, truePriceL = null;
            double? zapS = null, zapL = null;
            double? sigmaZapS = null, sigmaZapL = null;
            bool? shortCand = null, longCand = null;

            if (beta.HasValue)
            {
                if (benchAskPct.HasValue) truePriceS = benchAskPct.Value * beta.Value;
                if (benchBidPct.HasValue) truePriceL = benchBidPct.Value * beta.Value;
            }

            if (truePriceS.HasValue && bidPct.HasValue) zapS = bidPct.Value - truePriceS.Value;
            if (truePriceL.HasValue && askPct.HasValue) zapL = askPct.Value - truePriceL.Value;

            if (sigma.HasValue && sigma.Value != 0)
            {
                if (zapS.HasValue) sigmaZapS = zapS.Value / sigma.Value;
                if (zapL.HasValue) sigmaZapL = zapL.Value / sigma.Value;
            }

            if (zapS.HasValue) shortCand = zapS.Value > 0;
            if (zapL.HasValue) longCand = zapL.Value < 0;

            var mid = (bid.HasValue && ask.HasValue) ? (double?)((bid.Value + ask.Value) / 2.0) : null;
            double? spreadBps = null;
            if (mid.HasValue && spread.HasValue && mid.Value != 0) spreadBps = spread.Value / mid.Value * 10000.0;

            var newsCnt = TryGetInt("NewsCnt", "LstClsNewsCnt");
            var hasNews = newsCnt.HasValue ? newsCnt.Value > 0 : (bool?)null;

            bool? isCrap = null;
            if (lstClose.HasValue) isCrap = lstClose.Value < 5.0;

            return new TapeRowV1
            {
                DateNy = dateNy,
                MinuteIdx = minuteIdx,
                MinuteNy = minuteNy,
                Band = band,
                Ticker = item.Ticker,

                Bid = bid,
                Ask = ask,
                Mid = mid,
                Spread = spread,
                SpreadBps = spreadBps,

                BidPct = bidPct,
                AskPct = askPct,

                LstPrcLstClsPct = TryGetDouble("LstPrcLstClsΔ%"),
                LstPrcTOpenPct = TryGetDouble("LstPrcTOpenΔ%"),
                TOpen = TryGetDouble("TOpen"),
                TCls = TryGetDouble("TCls"),
                ATR14 = TryGetDouble("ATR14"),
                Hi = TryGetDouble("Hi"),

                LstClose = lstClose, // legacy alias
                YCls = yCls,
                LstCls = lstCls,

                Gap = gap,
                GapPct = gapPct,
                ClsToClsPct = clsToClsPct,

                VWAP = TryGetDouble("VWAP"),
                Lo = TryGetDouble("Lo"),


                Vol = TryGetDouble("Vol"),
                PreMktVol = TryGetDouble("PreMhVol", "PreMktVol"),
                PreMktVolNF = TryGetDouble("PreMhVolNF"),
                Adv20 = TryGetDouble("ADV20"),
                Adv90 = TryGetDouble("ADV90"),
                Adv20NF = TryGetDouble("ADV20NF"),
                Adv90NF = TryGetDouble("ADV90NF"),

                IsPTP = TryGetBool("IsPTP"),
                IsSSR = TryGetBool("SSR"),
                OutThSSR = TryGetBool("OutThSSR"),
                IsETF = TryGetBool("ETF"),
                HasReport = TryGetBool("Report"),
                NewsCnt = newsCnt,
                HasNews = hasNews,
                IsCrap = isCrap,

                BenchTicker = benchTicker,
                BenchBid = benchBid,
                BenchAsk = benchAsk,
                BenchLstCls = benchLst,
                BenchBidPct = benchBidPct,
                BenchAskPct = benchAskPct,

                ZapPctS = zapS,
                ZapPctL = zapL,
                SigmaZapS = sigmaZapS,
                SigmaZapL = sigmaZapL,
                TruePriceS = truePriceS,
                TruePriceL = truePriceL,
                ShortCandidate = shortCand,
                LongCandidate = longCand,

                Sig = TryGetDouble("Sig", "Sigmazap", "SigmazapS"),

                TierBp = TryGetDouble("TierBP"),
                MarketCapM = TryGetDouble("MarketCapM"),
                Exchange = meta.TryGetValue("Exchange", out var ex) ? ex?.ToString() : null,
                Country = meta.TryGetValue("Country", out var c) ? c?.ToString() : null,
                SectorL3 = meta.TryGetValue("SectorL3", out var s3) ? s3?.ToString() : null,
                RoundLot = TryGetInt("RoundLot"),

                Beta = beta,
                Sigma = sigma
            };
        }

        private static async Task WriteParquetAsync(string filePath, List<TapeRowV1> rows, CancellationToken ct)
        {
            await using var fs = File.Create(filePath);

            var opts = new ParquetSerializerOptions
            {
                CompressionMethod = CompressionMethod.None
            };

            await ParquetSerializer.SerializeAsync(rows, fs, opts, ct);
        }
    }
}
