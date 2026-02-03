using System.Collections.Generic;

namespace TradingBridgeApi.Options
{
    public sealed class TickerdaysOptions
    {
        public JobsOptions Jobs { get; set; } = new();
        public List<TickerdaysWindow> Windows { get; set; } = new();

        /// <summary>
        /// Intraday payload limits/options (for ChartsFeed data).
        /// </summary>
        public IntradayOptions Intraday { get; set; } = new();

        public sealed class JobsOptions
        {
            public int TtlMinutes { get; set; } = 120;
            public int MaxResultsInMemory { get; set; } = 50;
        }

        public sealed class IntradayOptions
        {
            /// <summary>
            /// Hard cap on how many ticker-days to include in Intraday payload.
            /// Prevents huge responses and UI overload.
            /// </summary>
            public int MaxTickerDays { get; set; } = 200;
        }

        public sealed class TickerdaysWindow
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int MinuteFrom { get; set; } // 0..1199
            public int MinuteTo { get; set; }   // 0..1199
        }
    }
}
