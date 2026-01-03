using System.Collections.Generic;

namespace TradingBridgeApi.StrategyCommon.Dtos;

public sealed class BestParamsDto
{
    public string Class { get; set; } = "";
    public string ClassMapped { get; set; } = "";

    public decimal? Rating { get; set; }
    public int Total { get; set; }

    // Arbitrage-only (HS). For others keep 0.
    public int Hard { get; set; }
    public int Soft { get; set; }

    public string Hs { get; set; } = "H";

    public decimal? Beta { get; set; }
    public decimal? Sigma { get; set; }

    public decimal? PrintMedianPos { get; set; }
    public decimal? PrintMedianNeg { get; set; }

    public List<BestRangeDto> DevPos { get; set; } = new();
    public List<BestRangeDto> DevNeg { get; set; } = new();
    public List<BestRangeDto> BenchPos { get; set; } = new();
    public List<BestRangeDto> BenchNeg { get; set; } = new();
}

public sealed class BestRangeDto
{
    public decimal Min { get; set; }
    public decimal Max { get; set; }
}
