namespace TradingBridgeApi.Dtos.Sifter;

public sealed class SifterDaysRequestDto
{
    public DateOnly FromDateNy { get; set; }
    public DateOnly ToDateNy { get; set; }

    public double? MinGapPct { get; set; }
    public double? MaxGapPct { get; set; }

    public double? MinClsToClsPct { get; set; }
    public double? MaxClsToClsPct { get; set; }

    public double? MinMarketCapM { get; set; }
    public double? MaxMarketCapM { get; set; }

    public List<string>? SectorsL3 { get; set; }
    public List<string>? Tickers { get; set; }
}
