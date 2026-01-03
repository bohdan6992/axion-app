using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Signals;

public sealed class TopModePolicy
{
    public IEnumerable<SignalItemDto> Apply(IEnumerable<SignalItemDto> items, SignalsQueryDto q)
    {
        if (!string.Equals(q.Mode, "top", StringComparison.OrdinalIgnoreCase))
            return items;

        // Mode=top means:
        // - items MUST already be marked as passing top gates by strategy-specific join logic
        // We rely on item.TopOk (or a similar flag) if you have it;
        // otherwise use item.BestRangesOk booleans that are already computed.

        return items.Where(x =>
        {
            // safest: if any of these exists, keep it.
            if (x.ShortRangesOk == true || x.LongRangesOk == true) return true;

            // if you add explicit TopOk in the DTO later — plug it here.
            return false;
        });
    }
}
