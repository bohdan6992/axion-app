namespace TradingBridgeApi.Dtos.Sifter;

public sealed class SifterWindowResponseDto
{
    public Dictionary<string, MetricStatsDto> Summary { get; set; } = new();
    public List<ByDayDto> ByDay { get; set; } = new();
    public List<ByTickerDto> ByTicker { get; set; } = new();
    public Dictionary<string, HistogramDto>? Hist { get; set; }
}

public sealed class MetricStatsDto
{
    public long Count { get; set; }
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? P05 { get; set; }
    public double? P95 { get; set; }
}

public sealed class ByDayDto
{
    public DateOnly DateNy { get; set; }
    public long Count { get; set; }
    public Dictionary<string, double?> Mean { get; set; } = new();
    public Dictionary<string, double?> Median { get; set; } = new();
}

public sealed class ByTickerDto
{
    public string Ticker { get; set; } = "";
    public long Count { get; set; }
    public Dictionary<string, double?> Mean { get; set; } = new();
    public Dictionary<string, double?> Median { get; set; } = new();
}

public sealed class HistogramDto
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double[] BinEdges { get; set; } = Array.Empty<double>();
    public long[] Counts { get; set; } = Array.Empty<long>();
}
