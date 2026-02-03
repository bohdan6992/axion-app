using System;

namespace TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models
{
    public enum TapeArbClass
    {
        BLUE,
        ARK,
        INTRA,
        POST,
        NIGHT,
        GLOB
    }

    public static class TapeArbClasses
    {
        // minuteIdx: 0 = 00:00 NY

        // BLUE: 00:01–03:59 -> 1..239
        public const int BlueFrom = 1;
        public const int BlueTo = 3 * 60 + 59; // 239

        // ARK: 04:01–09:25 -> 241..565
        public const int ArkFrom = 4 * 60 + 1;      // 241
        public const int ArkTo = 9 * 60 + 25;       // 565

        // INTRA: 10:01–15:59 -> 601..959
        public const int IntraFrom = 10 * 60 + 1;   // 601
        public const int IntraTo = 15 * 60 + 59;    // 959

        // POST: 16:01–19:59 -> 961..1199
        public const int PostFrom = 16 * 60 + 1;    // 961
        public const int PostTo = 19 * 60 + 59;     // 1199

        // NIGHT: 20:01–23:59 -> 1201..1439
        // (поки writer пише тільки 0..1199, але клас фіксуємо наперед)
        public const int NightFrom = 20 * 60 + 1;   // 1201
        public const int NightTo = 23 * 60 + 59;    // 1439

        // GLOB: весь діапазон доби
        public const int GlobFrom = 0;
        public const int GlobTo = 23 * 60 + 59;     // 1439

        // PrintNorm: 09:29–09:31 -> 569..571
        public const int PrintFrom = 9 * 60 + 29;   // 569
        public const int PrintTo = 9 * 60 + 31;     // 571

        // OpenNorm: 09:31–10:00 -> 571..600
        public const int OpenFrom = 9 * 60 + 31;    // 571
        public const int OpenTo = 10 * 60;          // 600

        public static TapeArbClass ClassByStartMinute(int minuteIdx)
        {
            if (minuteIdx >= BlueFrom && minuteIdx <= BlueTo) return TapeArbClass.BLUE;
            if (minuteIdx >= ArkFrom && minuteIdx <= ArkTo) return TapeArbClass.ARK;
            if (minuteIdx >= IntraFrom && minuteIdx <= IntraTo) return TapeArbClass.INTRA;
            if (minuteIdx >= PostFrom && minuteIdx <= PostTo) return TapeArbClass.POST;
            if (minuteIdx >= NightFrom && minuteIdx <= NightTo) return TapeArbClass.NIGHT;

            return TapeArbClass.GLOB;
        }

        public static (int From, int To) Window(TapeArbClass cls)
        {
            return cls switch
            {
                TapeArbClass.BLUE  => (BlueFrom, BlueTo),
                TapeArbClass.ARK   => (ArkFrom, ArkTo),
                TapeArbClass.INTRA => (IntraFrom, IntraTo),
                TapeArbClass.POST  => (PostFrom, PostTo),
                TapeArbClass.NIGHT => (NightFrom, NightTo),
                TapeArbClass.GLOB  => (GlobFrom, GlobTo),
                _                  => (GlobFrom, GlobTo)
            };
        }

        public static TapeArbClass? Next(TapeArbClass cls)
        {
            return cls switch
            {
                TapeArbClass.BLUE  => TapeArbClass.ARK,
                TapeArbClass.ARK   => TapeArbClass.INTRA,
                TapeArbClass.INTRA => TapeArbClass.POST,
                TapeArbClass.POST  => TapeArbClass.NIGHT,
                TapeArbClass.NIGHT => null,
                TapeArbClass.GLOB  => null,
                _                  => null
            };
        }

        public static bool InWindow(int minuteIdx, TapeArbClass cls)
        {
            var (from, to) = Window(cls);
            return minuteIdx >= from && minuteIdx <= to;
        }

        public static bool IsInPrintWindow(int minuteIdx)
            => minuteIdx >= PrintFrom && minuteIdx <= PrintTo;

        public static bool IsInOpenWindow(int minuteIdx)
            => minuteIdx >= OpenFrom && minuteIdx <= OpenTo;
    }
}
