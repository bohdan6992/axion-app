using System;

namespace TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models
{
    public sealed class TapeArbParams
    {
        public string DateNy { get; set; } = "";
        public TapeArbMetric Metric { get; set; } = TapeArbMetric.SigmaZap;

        // Start: abs(dev) >= StartAbs
        // Базовий поріг: 0.10 (можна тільки підвищувати на фронті)
        public double StartAbs { get; set; } = 0.10;

        // End: abs(dev) <= EndAbs
        // Базовий поріг: 0.05 (можна тільки підвищувати на фронті)
        public double EndAbs { get; set; } = 0.05;

        // optional filters like live signals
        public double? MinRate { get; set; } = null;
        public int? MinTotal { get; set; } = null;

        // optional ticker filter list
        public string[]? Tickers { get; set; } = null;
    }

    public sealed class TapeArbActive
    {
        public TapeArbStatus Status { get; set; } = TapeArbStatus.Active;

        public string DateNy { get; set; } = "";
        public int MinuteIdx { get; set; }          // last seen minute

        public string Ticker { get; set; } = "";
        public string BenchTicker { get; set; } = "";

        public TapeArbSide Side { get; set; }

        public double StartDev { get; set; }
        public int StartMinuteIdx { get; set; }

        public double PeakDevAbs { get; set; }
        public double PeakDev { get; set; }
        public int PeakMinuteIdx { get; set; }

        public double LastDev { get; set; }

        // static gating
        public double? Rating { get; set; }
        public int? Total { get; set; }

        // sizing
        public double? TierBp { get; set; }
        public double? Beta { get; set; }
        public double? HedgeNotional { get; set; }

        // prices at start (for later PnL on close)
        public double? StockEntryPct { get; set; }
        public double? BenchEntryPct { get; set; }
    }

    public sealed class TapeArbClosed
    {
        public TapeArbStatus Status { get; set; } = TapeArbStatus.Closed;

        public string DateNy { get; set; } = "";

        public string Ticker { get; set; } = "";
        public string BenchTicker { get; set; } = "";
        public TapeArbSide Side { get; set; }

        public int StartMinuteIdx { get; set; }
        public int PeakMinuteIdx { get; set; }
        public int EndMinuteIdx { get; set; }

        public double StartDev { get; set; }
        public double PeakDev { get; set; }
        public double EndDev { get; set; }

        public double? Rating { get; set; }
        public int? Total { get; set; }

        // sizing
        public double? TierBp { get; set; }
        public double? Beta { get; set; }
        public double? HedgeNotional { get; set; }

        // entry/exit pct
        public double? StockEntryPct { get; set; }
        public double? StockExitPct { get; set; }

        public double? BenchEntryPct { get; set; }
        public double? BenchExitPct { get; set; }

        // pnl
        public double? StockPnlUsd { get; set; }
        public double? HedgePnlUsd { get; set; }
        public double? TotalPnlUsd { get; set; }

        // ---- НОВІ ПОЛЯ ДЛЯ КЛАСІВ / НОРМАЛІЗАЦІЇ ----

        // Клас, в якому стартувала подія (BLUE/ARK/INTRA/POST/NIGHT/GLOB)
        public TapeArbClass StartClass { get; set; }

        // Чи відбулась нормалізація (EndAbs) до кінця стартового класу
        public bool NormInSameClass { get; set; }

        // Чи відбулась нормалізація в наступному класі
        public bool NormInNextClass { get; set; }

        // Для BLUE/ARK: нормалізація в Print-вікні (09:29–09:31)
        public bool PrintNorm { get; set; }

        // Для BLUE/ARK: нормалізація в Open-вікні (09:31–10:00)
        public bool OpenNorm { get; set; }

        // Мінімальне |dev| у межах класу старту + його хвилина
        public double MinAbsDevInClass { get; set; }
        public int MinAbsDevMinuteIdxInClass { get; set; }

        // dev (zapSig) на кінці класу для INTRA/POST/NIGHT (якщо доступний)
        public double? EndDevAtClassEnd { get; set; }
    }

    public sealed class TapeArbState
    {
        public bool IsActive { get; set; }

        public TapeArbSide Side { get; set; }

        public int StartMinuteIdx { get; set; }
        public double StartDev { get; set; }

        public int PeakMinuteIdx { get; set; }
        public double PeakDevAbs { get; set; }
        public double PeakDev { get; set; }

        public double LastDev { get; set; }
        public int LastMinuteIdx { get; set; }

        public string BenchTicker { get; set; } = "";

        // static
        public double? Rating { get; set; }
        public int? Total { get; set; }

        // sizing
        public double? TierBp { get; set; }
        public double? Beta { get; set; }
        public double? HedgeNotional { get; set; }

        // entry pct for PnL
        public double? StockEntryPct { get; set; }
        public double? BenchEntryPct { get; set; }

        // ---- НОВІ ПОЛЯ ДЛЯ КЛАСІВ / НОРМАЛІЗАЦІЇ ----

        // Клас старту епізоду
        public TapeArbClass StartClass { get; set; }

        // Хвилина завершення стартового класу (кеш, щоб не рахувати кожного разу)
        public int StartClassEndMinuteIdx { get; set; }

        // Мінімальне |dev| у класі старту + хвилина
        public double MinAbsDevInClass { get; set; }
        public int MinAbsDevMinuteIdxInClass { get; set; }

        // dev на кінці класу (для INTRA/POST/NIGHT)
        public double? EndDevAtClassEnd { get; set; }
    }
}
