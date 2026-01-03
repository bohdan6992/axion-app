namespace TradingBridgeApi.StrategyCommon.Dtos;

public sealed class SignalsQueryDto
{
    public string Strategy { get; set; } = "";
    public string Class { get; set; } = "global";   // ark/print/open/intra/global...
    public string Type { get; set; } = "any";       // any/hard/soft (strategy-specific)
    public string Mode { get; set; } = "all";       // all/top (optional)
    public decimal MinRate { get; set; } = 0.3m;
    public int MinTotal { get; set; } = 3;
    public int TopN { get; set; } = 30;

    // ✅ optional (controller/service can use these)
    public string? Tickers { get; set; }            // "AAPL,MSFT"
    public int? Limit { get; set; }                 // pagination
    public int? Offset { get; set; }                // pagination
}
