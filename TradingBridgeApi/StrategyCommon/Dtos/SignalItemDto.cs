using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBridgeApi.StrategyCommon.Dtos;

public sealed class SignalItemDto
{
    public string Strategy { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Benchmark { get; set; } = "";
    public string BetaBucket { get; set; } = "";
    public string TimeWindow { get; set; } = "";
    public string Direction { get; set; } = "none";
    public string Exchange { get; set; } = "";
    public decimal Rating { get; set; }
    public int Total { get; set; }


    public Dictionary<string, object?> Meta { get; set; } = new();
    public BestParamsDto? Best { get; set; }

    [JsonPropertyName("best_params")]
    public JsonElement? BestParamsRow { get; set; }

    public decimal? BidStock { get; set; }
    public decimal? AskStock { get; set; }
    public decimal? BidBench { get; set; }
    public decimal? AskBench { get; set; }

    [JsonPropertyName("BidLstClsΔ%")]
    public decimal? BidLstClsDeltaPct { get; set; }

    [JsonPropertyName("AskLstClsΔ%")]
    public decimal? AskLstClsDeltaPct { get; set; }

    [JsonPropertyName("Bid")]
    public decimal? Bid { get; set; }

    [JsonPropertyName("Ask")]
    public decimal? Ask { get; set; }

    public decimal? DevS { get; set; }
    public decimal? DevL { get; set; }

    public decimal? STruePrice { get; set; }
    public decimal? LTruePrice { get; set; }

    public decimal? ZapS { get; set; }
    public decimal? ZapL { get; set; }
    public bool ShortCandidate { get; set; }
    public bool LongCandidate { get; set; }

    public decimal? Sig { get; set; }
    public decimal? ZapSsigma { get; set; }
    public decimal? ZapLsigma { get; set; }

    public decimal? DevSsig { get; set; }
    public decimal? DevLsig { get; set; }

    public decimal? PrintSigmaPos { get; set; }
    public decimal? PrintSigmaNeg { get; set; }
    public bool? ShortSigmaOk { get; set; }
    public bool? LongSigmaOk { get; set; }

    public string DevBestRanges { get; set; } = "";
    public bool? ShortRangesOk { get; set; }
    public bool? LongRangesOk { get; set; }

    public string BenchBestRanges { get; set; } = "";
    public decimal? BenchDevShort { get; set; }
    public decimal? BenchDevLong { get; set; }
    public bool? ShortBenchOk { get; set; }
    public bool? LongBenchOk { get; set; }
    public decimal? BenchRefDev { get; set; }

    public string Hs { get; set; } = "H";
    public bool IsPtp { get; set; }
    public bool IsSsr { get; set; }
    public bool OutThSsr { get; set; }
    public decimal? Spread { get; set; }
    public bool HasReport { get; set; }
    public bool IsEtf { get; set; }
}
