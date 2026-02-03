namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeQueryRequest
    {
        public string DateNy { get; init; } = string.Empty;

        // inclusive minute range (0..1199)
        public int? MinuteFrom { get; init; }
        public int? MinuteTo { get; init; }

        public string[]? Tickers { get; init; }

        // NOTE: these are generic filters; for arbitrage, do direction-specific checks in PnL service
        public double? MinZapPct { get; init; }
        public double? MinSigmaZap { get; init; }

        // 0 => no cap
        public int Limit { get; init; } = 0;
    }
}
