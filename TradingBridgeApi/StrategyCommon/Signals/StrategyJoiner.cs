using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Signals;

public sealed class StrategyJoiner
{
    public IEnumerable<SignalItemDto> Join(
        object liveSnapshot,
        Dictionary<string, BestParamsDto> bestByTicker,
        string strategy,
        SignalsQueryDto q)
    {
        var liveItems = ExtractLiveItems(liveSnapshot);

        foreach (var li in liveItems)
        {
            var ticker = (li.Ticker ?? "").Trim().ToUpperInvariant();
            if (ticker.Length == 0) continue;

            bestByTicker.TryGetValue(ticker, out var best);

            var row = li.Fields ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var meta = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);

            var it = new SignalItemDto
            {
                Strategy = strategy,
                Ticker = ticker,
                Exchange = TryGetStringFlex(row, "Exchange") ?? "",
                Meta = meta,
                Best = best,

                // will set below
                Benchmark = "",
                Rating = 0,
                Total = 0
            };

            // ----------------------------
            // benchmark ticker (injected into row/meta earlier)
            // ----------------------------
            it.Benchmark =
                (TryGetStringFlex(row, "Benchmark")
                 ?? TryGetStringFlex(row, "bench.ticker")
                 ?? TryGetStringFlex(meta, "bench.ticker")
                 ?? "")
                .Trim()
                .ToUpperInvariant();

            // ----------------------------
            // 1) STOCK raw devs + quotes
            // ----------------------------
            var bidDev = TryGetDecimalFlex(row, "BidLstClsΔ%");
            var askDev = TryGetDecimalFlex(row, "AskLstClsΔ%");

            it.BidStock = bidDev;
            it.AskStock = askDev;

            // IMPORTANT: these are the ones that serialize as "BidLstClsΔ%" / "AskLstClsΔ%"
            it.BidLstClsDeltaPct = bidDev;
            it.AskLstClsDeltaPct = askDev;

            it.Bid = TryGetDecimalFlex(row, "Bid");
            it.Ask = TryGetDecimalFlex(row, "Ask");

            // ----------------------------
            // 2) BENCH devs
            //
            // ✅ FIX: prefer "flat" keys injected into STOCK row:
            //   BenchBidLstClsΔ% / BenchAskLstClsΔ%
            // then fallback to old "bench.*" keys (either in row or meta).
            // This removes the dependency on "attach after join" ordering.
            // ----------------------------
            it.BidBench =
                TryGetDecimalFlex(row, "BenchBidLstClsΔ%")
                ?? TryGetDecimalFlex(row, "bench.BidLstClsΔ%")
                ?? TryGetDecimalFlex(meta, "bench.BidLstClsΔ%");

            it.AskBench =
                TryGetDecimalFlex(row, "BenchAskLstClsΔ%")
                ?? TryGetDecimalFlex(row, "bench.AskLstClsΔ%")
                ?? TryGetDecimalFlex(meta, "bench.AskLstClsΔ%");

            // keep backward-compatible aliases
            // (so UI can still read bench devs from meta)
            if (!meta.ContainsKey("bench.BidLstClsΔ%") && it.BidBench.HasValue)
                meta["bench.BidLstClsΔ%"] = it.BidBench.Value;

            if (!meta.ContainsKey("bench.AskLstClsΔ%") && it.AskBench.HasValue)
                meta["bench.AskLstClsΔ%"] = it.AskBench.Value;

            if (!meta.ContainsKey("bench.ticker") && it.Benchmark.Length > 0)
                meta["bench.ticker"] = it.Benchmark;

            // ----------------------------
            // 3) rating/total/beta/sigma from Best snapshot
            // ----------------------------
            if (best is not null)
            {
                it.Rating = (decimal)(best.Rating ?? 0m);
                it.Total = best.Total;

                it.Hs = string.IsNullOrWhiteSpace(best.Hs)
                    ? (q.Type == "hard" ? "H" : (q.Type == "soft" ? "S" : "A"))
                    : best.Hs;

                it.Sig = best.Sigma;
            }
            else
            {
                it.Hs = q.Type == "hard" ? "H" : (q.Type == "soft" ? "S" : "A");
            }

            var beta = best?.Beta;
            var sigma = best?.Sigma;

            // ----------------------------
            // 4) true prices + ZAP (legacy: bench dev * beta)
            // ----------------------------
            if (beta.HasValue)
            {
                if (it.AskBench.HasValue)
                    it.STruePrice = it.AskBench.Value * beta.Value;

                if (it.BidBench.HasValue)
                    it.LTruePrice = it.BidBench.Value * beta.Value;
            }

            if (it.BidStock.HasValue && it.STruePrice.HasValue)
                it.ZapS = it.BidStock.Value - it.STruePrice.Value;

            // ✅ ETALON SIGN: ZapL = AskStock - LTruePrice  (long when ZapL < 0)
            if (it.LTruePrice.HasValue && it.AskStock.HasValue)
                it.ZapL = it.AskStock.Value - it.LTruePrice.Value;

            it.ShortCandidate =
                it.BidStock.HasValue && it.STruePrice.HasValue &&
                it.BidStock.Value > it.STruePrice.Value;

            // ✅ ETALON: LongCandidate = ZapL < 0
            it.LongCandidate =
                it.ZapL.HasValue && it.ZapL.Value < 0;

            // ----------------------------
            // 5) normalize by sigma
            // ----------------------------
            if (sigma.HasValue && sigma.Value != 0)
            {
                if (it.ZapS.HasValue) it.ZapSsigma = it.ZapS.Value / sigma.Value;
                if (it.ZapL.HasValue) it.ZapLsigma = it.ZapL.Value / sigma.Value;
            }

            // legacy aliases (keep same semantics as before)
            it.DevS = it.AskStock;
            it.DevL = it.BidStock;
            it.DevSsig = it.ZapSsigma;
            it.DevLsig = it.ZapLsigma;

            // ----------------------------
            // 5.5) sigma gate (MODE=ALL) - 1:1 with old bridge
            //
            // non-print: abs(zap*sigma) >= 0.1
            // print: abs(zap*sigma) >= best.PrintMedianPos/Neg (fallback 0.1)
            // ----------------------------
            {
                const decimal MIN_SIGMA_ABS_OTHER = 0.1m;
                const decimal FALLBACK_PRINT_POS = 0.1m;
                const decimal FALLBACK_PRINT_NEG = 0.1m;

                var cls = (q.Class ?? "global").Trim().ToLowerInvariant();
                var isPrint = cls == "print";

                decimal thrPosAbs;
                decimal thrNegAbs;

                if (!isPrint)
                {
                    thrPosAbs = MIN_SIGMA_ABS_OTHER;
                    thrNegAbs = MIN_SIGMA_ABS_OTHER;
                }
                else
                {
                    thrPosAbs = best?.PrintMedianPos ?? FALLBACK_PRINT_POS;
                    thrNegAbs = Math.Abs(best?.PrintMedianNeg ?? FALLBACK_PRINT_NEG);
                }

                it.PrintSigmaPos = thrPosAbs;
                it.PrintSigmaNeg = thrNegAbs;

                it.ShortSigmaOk =
                    it.ShortCandidate &&
                    it.ZapSsigma.HasValue &&
                    Math.Abs(it.ZapSsigma.Value) >= thrPosAbs;

                it.LongSigmaOk =
                    it.LongCandidate &&
                    it.ZapLsigma.HasValue &&
                    Math.Abs(it.ZapLsigma.Value) >= thrNegAbs;
            }

            // ----------------------------
            // 6) ranges checks (if present)
            // ----------------------------
            var devPos = best?.DevPos ?? new List<BestRangeDto>();
            var devNeg = best?.DevNeg ?? new List<BestRangeDto>();

            it.DevBestRanges = BuildRangesLabel(devPos, devNeg);

            if (it.ZapSsigma.HasValue && devPos.Count > 0)
                it.ShortRangesOk = InAnyRangeAbs(it.ZapSsigma.Value, devPos);

            if (it.ZapLsigma.HasValue && devNeg.Count > 0)
                it.LongRangesOk = InAnyRangeAbs(it.ZapLsigma.Value, devNeg);

            var benchPos = best?.BenchPos ?? new List<BestRangeDto>();
            var benchNeg = best?.BenchNeg ?? new List<BestRangeDto>();

            it.BenchBestRanges = BuildBenchRangesLabel(benchPos, benchNeg);

            it.BenchDevShort = it.BidBench;
            it.BenchDevLong = it.AskBench;

            // legacy TOP bench gate uses ABS ranges (1:1 with old StrategySignalService)
            if (it.BenchDevShort.HasValue && benchPos.Count > 0)
                it.ShortBenchOk = InAnyRangeAbs(it.BenchDevShort.Value, benchPos);

            if (it.BenchDevLong.HasValue && benchNeg.Count > 0)
                it.LongBenchOk = InAnyRangeAbs(it.BenchDevLong.Value, benchNeg);

            it.BenchRefDev = it.BenchDevShort ?? it.BenchDevLong;

            // ----------------------------
            // 6.5) Direction (1:1 with old bridge)
            // - mode=all: based on SigmaOk
            // - mode=top: SigmaOk + RangesOk + BenchOk (strict)
            // If both sides ok -> choose by larger abs(zap/sigma).
            // ----------------------------
            {
                var mode = (q.Mode ?? "all").Trim().ToLowerInvariant();
                var wantTop = mode == "top";

                bool shortOk = (it.ShortSigmaOk ?? false);
                bool longOk = (it.LongSigmaOk ?? false);

                if (wantTop)
                {
                    shortOk = shortOk && (it.ShortRangesOk ?? false) && (it.ShortBenchOk ?? false);
                    longOk = longOk && (it.LongRangesOk ?? false) && (it.LongBenchOk ?? false);
                }

                if (shortOk && !longOk) it.Direction = "short";
                else if (!shortOk && longOk) it.Direction = "long";
                else if (shortOk && longOk)
                {
                    var s = it.ZapSsigma.HasValue ? Math.Abs(it.ZapSsigma.Value) : 0m;
                    var l = it.ZapLsigma.HasValue ? Math.Abs(it.ZapLsigma.Value) : 0m;
                    it.Direction = (s >= l) ? "short" : "long";
                }
                else
                {
                    it.Direction = "none";
                }
            }

            // ----------------------------
            // 7) LIVE flags
            // ----------------------------
            it.IsPtp = Eq(TryGetStringFlex(row, "IsPTP"), "YES");
            it.IsSsr = Eq(TryGetStringFlex(row, "SSR"), "YES");
            it.OutThSsr = Eq(TryGetStringFlex(row, "OutThSSR"), "YES");

            it.Spread = TryGetDecimalFlex(row, "Spread");
            it.HasReport = Eq(TryGetStringFlex(row, "Report"), "YES");
            it.IsEtf = Eq(TryGetStringFlex(row, "ETF"), "YES");

            yield return it;
        }
    }

    // ----------------------------
    // helpers
    // ----------------------------

    private static List<LiveSnapshotItemDto> ExtractLiveItems(object liveSnapshot)
    {
        var prop = liveSnapshot.GetType().GetProperty("items");
        var val = prop?.GetValue(liveSnapshot);
        return val as List<LiveSnapshotItemDto> ?? new List<LiveSnapshotItemDto>();
    }

    private static bool Eq(string? a, string b) =>
        string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static string? TryGetStringFlex(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            if (!TryFindKeyByNormalized(row, key, out var realKey)) return null;
            if (!row.TryGetValue(realKey, out v) || v is null) return null;
        }

        if (v is string s) return s;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString();
            return je.ToString();
        }

        return v.ToString();
    }

    private static decimal? TryGetDecimalFlex(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null)
        {
            if (!TryFindKeyByNormalized(row, key, out var realKey)) return null;
            if (!row.TryGetValue(realKey, out v) || v is null) return null;
        }

        if (v is decimal d) return d;
        if (v is double dd) return (decimal)dd;
        if (v is float f) return (decimal)f;
        if (v is int i) return i;
        if (v is long l) return l;

        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
            {
                if (je.TryGetDecimal(out var dec)) return dec;
                if (je.TryGetDouble(out var dbl)) return (decimal)dbl;
                return null;
            }
            if (je.ValueKind == JsonValueKind.String)
                return ParseDecimalString(je.GetString());

            return null;
        }

        if (v is string s2)
            return ParseDecimalString(s2);

        return null;
    }

    private static decimal? ParseDecimalString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        s = s.Trim();
        s = s.Replace("−", "-").Replace("–", "-").Replace("—", "-");
        s = s.Replace(" ", "");
        if (s.EndsWith("%", StringComparison.Ordinal)) s = s[..^1];
        s = s.Replace(",", ".");

        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var x))
            return x;

        return null;
    }

    // Normalized-key matching: fixes Δ(U+0394) vs ∆(U+2206), spaces, etc.
    private static bool TryFindKeyByNormalized(Dictionary<string, object?> row, string wanted, out string foundKey)
    {
        foundKey = "";
        var w = NormalizeKey(wanted);

        foreach (var k in row.Keys)
        {
            if (NormalizeKey(k) == w)
            {
                foundKey = k;
                return true;
            }
        }
        return false;
    }

    private static string NormalizeKey(string s)
    {
        s ??= "";
        s = s.Trim();

        s = s.Replace("Δ", "D")
             .Replace("∆", "D")
             .Replace("％", "%");

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static bool InAnyRangeAbs(decimal value, List<BestRangeDto> ranges)
    {
        var v = Math.Abs(value);
        foreach (var r in ranges)
        {
            var lo = Math.Min(r.Min, r.Max);
            var hi = Math.Max(r.Min, r.Max);
            if (v >= lo && v <= hi) return true;
        }
        return false;
    }

    private static bool InAnyRange(decimal value, List<BestRangeDto> ranges)
    {
        foreach (var r in ranges)
        {
            var lo = Math.Min(r.Min, r.Max);
            var hi = Math.Max(r.Min, r.Max);
            if (value >= lo && value <= hi) return true;
        }
        return false;
    }

    private static string BuildRangesLabel(List<BestRangeDto> pos, List<BestRangeDto> neg)
    {
        static string F(List<BestRangeDto> rs) =>
            rs.Count == 0 ? "" : string.Join(", ", rs.Select(r => $"{r.Min:0.##}..{r.Max:0.##}"));

        var a = F(pos);
        var b = F(neg);

        if (a.Length == 0 && b.Length == 0) return "";
        if (a.Length > 0 && b.Length == 0) return $"POS[{a}]";
        if (a.Length == 0 && b.Length > 0) return $"NEG[{b}]";
        return $"POS[{a}] | NEG[{b}]";
    }

    private static string BuildBenchRangesLabel(List<BestRangeDto> pos, List<BestRangeDto> neg)
    {
        static string F(List<BestRangeDto> rs) =>
            rs.Count == 0 ? "" : string.Join(", ", rs.Select(r => $"{r.Min:0.##}..{r.Max:0.##}"));

        var a = F(pos);
        var b = F(neg);

        if (a.Length == 0 && b.Length == 0) return "";
        if (a.Length > 0 && b.Length == 0) return $"SHORT[{a}]";
        if (a.Length == 0 && b.Length > 0) return $"LONG[{b}]";
        return $"SHORT[{a}] | LONG[{b}]";
    }
}
