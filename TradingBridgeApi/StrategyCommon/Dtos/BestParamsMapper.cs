using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TradingBridgeApi.StrategyCommon.Dtos;

public static class BestParamsMapper
{
    public static BestParamsDto MapToSnapshot(
        JsonElement bestParamsRow,
        string clsRequested,
        string clsMapped,
        decimal? rating,
        int total,
        int hard = 0,
        int soft = 0,
        string hs = "H")
    {
        var dto = new BestParamsDto
        {
            Class = clsRequested,
            ClassMapped = clsMapped,
            Rating = rating,
            Total = total,
            Hard = hard,
            Soft = soft,
            Hs = hs
        };

        if (bestParamsRow.ValueKind != JsonValueKind.Object)
            return dto;

        // ----------------------------
        // static.beta / static.sigma
        // ----------------------------
        if (bestParamsRow.TryGetProperty("static", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            dto.Beta = TryGetDec(st, "beta");
            dto.Sigma = TryGetDec(st, "sigma");
        }

        // ----------------------------
        // dev_print_last5_median.pos/neg -> PrintMedianPos/Neg
        // (used for PRINT sigma gate in mode=all, matching old behavior)
        // ----------------------------
        if (bestParamsRow.TryGetProperty("dev_print_last5_median", out var pm) && pm.ValueKind == JsonValueKind.Object)
        {
            dto.PrintMedianPos = TryGetDec(pm, "pos");
            dto.PrintMedianNeg = TryGetDec(pm, "neg");
        }

        // ----------------------------
        // best_windows_any.stitched.(sigma_peak_bins / bench_peak_bins)
        //   .{cls}.(pos|neg)[] with {lo,hi}
        // -> DevPos/DevNeg + BenchPos/BenchNeg
        // Fallback to "global" if cls arrays are empty/missing.
        // ----------------------------
        var cls = (clsMapped ?? clsRequested ?? "global").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cls)) cls = "global";

        if (TryGet(bestParamsRow, "best_windows_any", out var bwa) &&
            bwa.ValueKind == JsonValueKind.Object &&
            TryGet(bwa, "stitched", out var stitched) &&
            stitched.ValueKind == JsonValueKind.Object)
        {
            dto.DevPos = ReadBinRanges(stitched, "sigma_peak_bins", cls, "pos");
            dto.DevNeg = ReadBinRanges(stitched, "sigma_peak_bins", cls, "neg");

            dto.BenchPos = ReadBinRanges(stitched, "bench_peak_bins", cls, "pos");
            dto.BenchNeg = ReadBinRanges(stitched, "bench_peak_bins", cls, "neg");
        }

        return dto;
    }

    private static List<BestRangeDto> ReadBinRanges(JsonElement stitched, string binsKey, string cls, string side)
    {
        if (!TryGet(stitched, binsKey, out var bins) || bins.ValueKind != JsonValueKind.Object)
            return new List<BestRangeDto>();

        // try cls first
        var rs = ReadRangesFromBins(bins, cls, side);
        if (rs.Count > 0) return rs;

        // fallback global
        if (!string.Equals(cls, "global", StringComparison.OrdinalIgnoreCase))
        {
            rs = ReadRangesFromBins(bins, "global", side);
            if (rs.Count > 0) return rs;
        }

        return new List<BestRangeDto>();
    }

    private static List<BestRangeDto> ReadRangesFromBins(JsonElement binsObj, string cls, string side)
    {
        var outList = new List<BestRangeDto>();

        if (!TryGet(binsObj, cls, out var clsObj) || clsObj.ValueKind != JsonValueKind.Object)
            return outList;

        if (!TryGet(clsObj, side, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return outList;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var lo = TryGetDec(item, "lo");
            var hi = TryGetDec(item, "hi");

            if (lo.HasValue && hi.HasValue)
                outList.Add(new BestRangeDto { Min = lo.Value, Max = hi.Value });
        }

        return outList;
    }

    private static bool TryGet(JsonElement obj, string key, out JsonElement el)
    {
        el = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out el);
    }

    private static decimal? TryGetDec(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var el)) return null;

        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetDecimal(out var d)) return d;
            if (el.TryGetDouble(out var dd)) return (decimal)dd;
        }
        return null;
    }

    public static List<BestRangeDto> ToRanges(IEnumerable<(decimal Min, decimal Max)> ranges)
    {
        var outList = new List<BestRangeDto>();
        foreach (var (mn, mx) in ranges)
            outList.Add(new BestRangeDto { Min = mn, Max = mx });
        return outList;
    }
}
