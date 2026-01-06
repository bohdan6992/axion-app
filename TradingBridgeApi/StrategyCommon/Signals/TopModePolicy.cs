using System;
using System.Collections.Generic;
using System.Linq;
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Signals;

public sealed class TopModePolicy
{
    public IEnumerable<SignalItemDto> Apply(IEnumerable<SignalItemDto> items, SignalsQueryDto q)
    {
        if (!string.Equals(q.Mode, "top", StringComparison.OrdinalIgnoreCase))
            return items;

        return items.Where(IsTopOk);
    }

    private static bool IsTopOk(SignalItemDto x)
    {
        // TOP (legacy arbitrage semantics):
        // require sigma gate + dev ranges + bench ranges (strict)
        var shortOk = x.ShortSigmaOk == true
                   && x.ShortRangesOk == true
                   && x.ShortBenchOk == true;

        var longOk  = x.LongSigmaOk == true
                   && x.LongRangesOk == true
                   && x.LongBenchOk == true;

        return shortOk || longOk;
    }
}
