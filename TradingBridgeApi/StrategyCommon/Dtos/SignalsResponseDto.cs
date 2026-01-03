using System.Collections.Generic;

namespace TradingBridgeApi.StrategyCommon.Dtos;

public sealed class SignalsResponseDto
{
    public string Strategy { get; set; } = "";
    public long GeneratedAt { get; set; }
    public int UniverseTickers { get; set; }
    public int ReturnedTickers { get; set; }
    public List<SignalItemDto> Items { get; set; } = new();
}
