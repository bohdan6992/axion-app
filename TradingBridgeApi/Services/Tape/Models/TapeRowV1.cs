using System;

namespace TradingBridgeApi.Services.Tape.Models
{
    public sealed record TapeRowV1
    {
        // Keys / time
        public string DateNy { get; init; } = string.Empty; // YYYY-MM-DD
        public int MinuteIdx { get; init; }
        public string MinuteNy { get; init; } = string.Empty; // "HH:mm"
        public string Band { get; init; } = string.Empty;
        public string Ticker { get; init; } = string.Empty;

        // Quotes (raw)
        public double? Bid { get; init; }
        public double? Ask { get; init; }
        public double? Mid { get; init; }
        public double? Spread { get; init; }
        public double? SpreadBps { get; init; }

        // Quotes (pct-space, for canonical arbitrage math)
        // Canon: BidPct = BidLstClsΔ%, AskPct = AskLstClsΔ%
        public double? BidPct { get; init; }
        public double? AskPct { get; init; }

        // SCOPE fill / move (pct-space)
        // Canon for SCOPE fill: LstPrcLstClsPct = LstPrcLstClsΔ%
        // Canon for move_1000:  LstPrcTOpenPct = LstPrcTOpenΔ%
        public double? LstPrcLstClsPct { get; init; }
        public double? LstPrcTOpenPct { get; init; }

        // Price context / TRAP daily context
        public double? TOpen { get; init; }
        public double? TCls { get; init; }
        public double? ATR14 { get; init; }
        public double? Hi { get; init; }

        // Price context (existing)
        public double? LstClose { get; init; }   // як і раніше, alias на LstCls/YCls

        // TRAP OSB day fields (нові)
        public double? YCls { get; init; }
        public double? LstCls { get; init; }

        public double? Gap { get; init; }
        public double? GapPct { get; init; }
        public double? ClsToClsPct { get; init; }

        public double? VWAP { get; init; }
        public double? Lo { get; init; }

        // Liquidity / volumes / notional
        public double? Vol { get; init; }
        public double? PreMktVol { get; init; }
        public double? PreMktVolNF { get; init; }
        public double? Adv20 { get; init; }
        public double? Adv90 { get; init; }
        public double? Adv20NF { get; init; }
        public double? Adv90NF { get; init; }

        // Flags
        public bool? IsPTP { get; init; }
        public bool? IsSSR { get; init; }
        public bool? OutThSSR { get; init; }
        public bool? IsETF { get; init; }
        public bool? HasReport { get; init; }
        public int? NewsCnt { get; init; }
        public bool? HasNews { get; init; }
        public bool? IsCrap { get; init; }

        // Bench (raw)
        public string? BenchTicker { get; init; }
        public double? BenchBid { get; init; }
        public double? BenchAsk { get; init; }
        public double? BenchLstCls { get; init; }

        // Bench (pct-space, for canonical arbitrage math)
        public double? BenchBidPct { get; init; }
        public double? BenchAskPct { get; init; }

        // Arbitrage-ready (canonical outputs; written to tape for downstream independence)
        public double? ZapPctS { get; init; }
        public double? ZapPctL { get; init; }
        public double? SigmaZapS { get; init; }
        public double? SigmaZapL { get; init; }
        public double? TruePriceS { get; init; }
        public double? TruePriceL { get; init; }
        public bool? ShortCandidate { get; init; }
        public bool? LongCandidate { get; init; }

        // Legacy alias (keep; some code may still read it)
        public double? Sig { get; init; }

        // Meta / static (duplicated per-minute for convenience)
        public double? TierBp { get; init; }
        public double? MarketCapM { get; init; }
        public string? Exchange { get; init; }
        public string? Country { get; init; }
        public string? SectorL3 { get; init; }
        public int? RoundLot { get; init; }

        // STATIC enrich (needed to compute canonical arbitrage fields deterministically)
        public double? Beta { get; init; }
        public double? Sigma { get; init; }
    }
}
