// Services/Strategy/Arbitrage/ArbitrageSummaryDtos.cs
using System;
using System.Collections.Generic;

namespace TradingBridgeApi.Services.Strategy.Arbitrage;

public sealed class SummaryResponse
{
    public DateTime? UpdatedAtUtc { get; set; }
    public int Count { get; set; }
    public string[] Header { get; set; } = Array.Empty<string>();
    public List<SummaryRow> Items { get; set; } = new();
}

public sealed class SummaryRow
{
    public string Ticker { get; set; } = "";
    public string Bench { get; set; } = "";
    public string Corr { get; set; } = "";
    public string Beta { get; set; } = "";
    public string Sig { get; set; } = "";

    public Dictionary<string, string> Extras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
