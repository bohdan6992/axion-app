namespace TradingBridgeApi.Dtos.Sifter;

public sealed class SifterWindowRequestDto
{
    public DateOnly FromDateNy { get; set; }
    public DateOnly ToDateNy { get; set; }

    public int MinuteFrom { get; set; }
    public int MinuteTo { get; set; }

    public List<string>? Tickers { get; set; }
    public double? MinMarketCapM { get; set; }
    public double? MaxMarketCapM { get; set; }
    public List<string>? SectorsL3 { get; set; }

    // тільки поля що вже є в тейпі/тейп-рядку
    public List<string> Metrics { get; set; } = new();

    // опційно
    public bool IncludeHist { get; set; } = true;
    public int HistBins { get; set; } = 60;
}
