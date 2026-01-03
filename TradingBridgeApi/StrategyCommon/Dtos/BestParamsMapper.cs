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

        if (bestParamsRow.ValueKind == JsonValueKind.Object &&
            bestParamsRow.TryGetProperty("static", out var st) &&
            st.ValueKind == JsonValueKind.Object)
        {
            if (st.TryGetProperty("beta", out var beta) && beta.ValueKind == JsonValueKind.Number && beta.TryGetDecimal(out var b))
                dto.Beta = b;

            if (st.TryGetProperty("sigma", out var sig) && sig.ValueKind == JsonValueKind.Number && sig.TryGetDecimal(out var s))
                dto.Sigma = s;
        }

        return dto;
    }

    public static List<BestRangeDto> ToRanges(IEnumerable<(decimal Min, decimal Max)> ranges)
    {
        var outList = new List<BestRangeDto>();
        foreach (var (mn, mx) in ranges)
            outList.Add(new BestRangeDto { Min = mn, Max = mx });
        return outList;
    }
}
