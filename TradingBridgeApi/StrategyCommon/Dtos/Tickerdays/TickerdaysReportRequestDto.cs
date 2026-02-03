using System;
using System.Collections.Generic;

namespace TradingBridgeApi.Dtos.Tickerdays
{
    public sealed class TickerdaysReportRequestDto
    {
        /// <summary>
        /// Preferred: NY trading date range (YYYY-MM-DD). This avoids timezone day-shifts.
        /// If provided, server will use these fields.
        /// </summary>
        public string? StartDateNy { get; set; }

        /// <summary>
        /// Preferred: NY trading date range (YYYY-MM-DD). This avoids timezone day-shifts.
        /// If provided, server will use these fields.
        /// </summary>
        public string? EndDateNy { get; set; }

        /// <summary>
        /// Legacy (kept for backward compatibility).
        /// Do NOT rely on timezone conversions here for dateNy semantics.
        /// If StartDateNy/EndDateNy are not provided, server will use StartDate.Date/EndDate.Date as-is.
        /// </summary>
        public DateTime StartDate { get; set; }

        /// <summary>
        /// Legacy (kept for backward compatibility).
        /// </summary>
        public DateTime EndDate { get; set; }

        public List<string> Tickers { get; set; } = new();

        public int FetchDataMode { get; set; } = 1;

        public TickerdaysFiltersDto Filters { get; set; } = new();

        public bool AdditionalPriceData { get; set; } = true;
        public bool AdditionalVolumeData { get; set; } = true;
        public bool AdditionalPriceDataWithParams { get; set; } = false;
    }

    public sealed class TickerdaysFiltersDto
    {
        public List<PricePercFilterDto> PricePercFilters { get; set; } = new();
        public List<GenericWindowFilterDto> VolatilityFilters { get; set; } = new();
        public List<GenericWindowFilterDto> VolumeFilters { get; set; } = new();
        public List<GenericWindowFilterDto> MoneyTradedFilters { get; set; } = new();

        public ReportFilterDto ReportFilter { get; set; } = new();
    }

    // 1:1 з твоїм JSON
    public sealed class PricePercFilterDto
    {
        public int DayIndex { get; set; } = 0;        // MVP: only 0
        public bool IsAbsChange { get; set; } = false;
        public double PricePercChange { get; set; } = 1.0;
        public int Side { get; set; } = 0;            // 0 any, 1 pos, 2 neg
        public int TimeStart { get; set; } = 0;        // window Id
        public int TimeEnd { get; set; } = 0;          // window Id
    }

    // Заглушка під майбутні блоки: volatility/volume/moneyTraded
    public sealed class GenericWindowFilterDto
    {
        public int DayIndex { get; set; } = 0;
        public bool Enabled { get; set; } = false;

        public double? Min { get; set; }
        public double? Max { get; set; }

        public int Side { get; set; } = 0;
        public int TimeStart { get; set; } = 0;
        public int TimeEnd { get; set; } = 0;
    }

    public sealed class ReportFilterDto
    {
        public int DayIndex { get; set; } = 0;
        public int ReportFilterType { get; set; } = 0;
    }
}
