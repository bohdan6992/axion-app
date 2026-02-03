using System.Collections.Generic;

namespace TradingBridgeApi.Dtos.Tickerdays
{
    public sealed class TickerdaysResultDto
    {
        public TickerdaysMetaDto Meta { get; set; } = new();
        public List<TickerdaysDayRowDto> Days { get; set; } = new();

        // key: "AAPL|2026-01-27"
        public Dictionary<string, List<TickerdaysIntradayPointDto>> Intraday { get; set; } = new();

        public TickerdaysPerformanceDto Performance { get; set; } = new();
    }

    public sealed class TickerdaysMetaDto
    {
        public string StartDateNy { get; set; } = "";
        public string EndDateNy { get; set; } = "";
        public List<string> Tickers { get; set; } = new();
        public int FetchDataMode { get; set; }
    }

    public sealed class TickerdaysDayRowDto
    {
        public string Ticker { get; set; } = "";
        public string DateNy { get; set; } = "";

        public double? PctChange { get; set; } // computed over filter window
        public List<string> Tags { get; set; } = new();

        // display-only meta from tape
        public double? MarketCapM { get; set; }
        public string? SectorL3 { get; set; }
        public string? Exchange { get; set; }
        public double? Adv20 { get; set; }

        public double? GapPct { get; set; }
        public double? ClsToClsPct { get; set; }
    }

    public sealed class TickerdaysIntradayPointDto
    {
        public string T { get; set; } = ""; // MinuteNy
        public double? C { get; set; }      // Mid (or fallback)
        public double? V { get; set; }      // Vol
    }

    public sealed class TickerdaysPerformanceDto
    {
        public List<TickerdaysTickerSummaryDto> Summary { get; set; } = new();
        public List<TickerdaysTradeDto> Trades { get; set; } = new();
    }

    public sealed class TickerdaysTickerSummaryDto
    {
        public string Ticker { get; set; } = "";
        public int Days { get; set; }
        public double WinRate { get; set; }
        public double Avg { get; set; }
        public double Median { get; set; }
    }

    public sealed class TickerdaysTradeDto
    {
        public string Ticker { get; set; } = "";
        public string DateNy { get; set; } = "";
        public double? PnlPct { get; set; }
        public List<string> Tags { get; set; } = new();
    }
}
