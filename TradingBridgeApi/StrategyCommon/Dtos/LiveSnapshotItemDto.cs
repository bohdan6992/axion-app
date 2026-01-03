using System.Collections.Generic;

namespace TradingBridgeApi.StrategyCommon.Dtos;

public sealed class LiveSnapshotItemDto
{
    public string Ticker { get; set; } = "";
    public Dictionary<string, object?> Fields { get; set; } = new();
}
