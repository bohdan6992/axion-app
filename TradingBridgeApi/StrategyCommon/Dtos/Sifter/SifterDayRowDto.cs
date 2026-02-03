namespace TradingBridgeApi.Dtos.Sifter;

public sealed class SifterDayRowDto
{
    public DateOnly DateNy { get; set; }
    public string Ticker { get; set; } = "";

    // TRAP OSB (готові)
    public double? TOpen { get; set; }
    public double? TCls { get; set; }
    public double? THi { get; set; }
    public double? TLo { get; set; }

    public double? YOpen { get; set; }
    public double? YCls { get; set; }
    public double? YHi { get; set; }
    public double? YLo { get; set; }

    public double? LstOpen { get; set; }
    public double? LstCls { get; set; }

    public double? Gap { get; set; }
    public double? GapPct { get; set; }
    public double? ClsToClsPct { get; set; }

    // Meta
    public double? Atr14 { get; set; }
    public long? Adv20 { get; set; }
    public long? Adv90 { get; set; }
    public long? PreMktVolNF { get; set; }

    public double? Beta { get; set; }
    public double? Sigma { get; set; }

    public double? MarketCapM { get; set; }
    public string? Exchange { get; set; }
    public string? SectorL3 { get; set; }
}
