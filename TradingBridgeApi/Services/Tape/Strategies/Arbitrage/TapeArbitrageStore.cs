using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models;

namespace TradingBridgeApi.Services.Tape.Strategies.Arbitrage
{
    // DTO returned by /api/tape/arbitrage/snapshot
    public sealed class TapeArbSnapshotDto
    {
        public bool Ok { get; set; }
        public string DateNy { get; set; } = "";
        public int LastMinuteIdx { get; set; }
        public int ActiveCount { get; set; }
        public int ClosedCount { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class TapeArbitrageStore
    {
        private sealed class DayBucket
        {
            public readonly object Gate = new();
            public readonly Dictionary<string, TapeArbState> StateByTicker = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<TapeArbClosed> Closed = new();
            public int LastMinuteIdxProcessed = -1;
            public DateTime UpdatedUtc = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, DayBucket> _byDateNy = new(StringComparer.OrdinalIgnoreCase);

        public (int lastMinute, int activeCount, int closedCount, DateTime updatedUtc) GetDayStats(string dateNy)
        {
            dateNy = (dateNy ?? "").Trim();

            if (!_byDateNy.TryGetValue(dateNy, out var b))
                return (-1, 0, 0, DateTime.UtcNow);

            lock (b.Gate)
            {
                var active = b.StateByTicker.Values.Count(x => x.IsActive);
                var closed = b.Closed.Count;
                return (b.LastMinuteIdxProcessed, active, closed, b.UpdatedUtc);
            }
        }

        // ✅ Used by TapeArbitrageController.GetSnapshot(...)
        public TapeArbSnapshotDto GetSnapshot(string dateNy)
        {
            dateNy = (dateNy ?? "").Trim();

            var (lastMinute, activeCount, closedCount, updatedUtc) = GetDayStats(dateNy);

            return new TapeArbSnapshotDto
            {
                Ok = true,
                DateNy = dateNy,
                LastMinuteIdx = lastMinute,
                ActiveCount = activeCount,
                ClosedCount = closedCount,
                UpdatedUtc = updatedUtc
            };
        }

        public void UpsertDay(string dateNy, int lastMinuteIdx, Dictionary<string, TapeArbState> states, List<TapeArbClosed> closed)
        {
            dateNy = (dateNy ?? "").Trim();

            var b = _byDateNy.GetOrAdd(dateNy, _ => new DayBucket());

            lock (b.Gate)
            {
                b.LastMinuteIdxProcessed = Math.Max(b.LastMinuteIdxProcessed, lastMinuteIdx);

                b.StateByTicker.Clear();
                foreach (var kv in states)
                    b.StateByTicker[kv.Key] = kv.Value;

                b.Closed.Clear();
                b.Closed.AddRange(closed);

                b.UpdatedUtc = DateTime.UtcNow;
            }
        }

        public IReadOnlyList<TapeArbActive> GetActive(string dateNy)
        {
            dateNy = (dateNy ?? "").Trim();

            if (!_byDateNy.TryGetValue(dateNy, out var b))
                return Array.Empty<TapeArbActive>();

            lock (b.Gate)
            {
                return b.StateByTicker
                    .Where(kv => kv.Value.IsActive)
                    .Select(kv => new TapeArbActive
                    {
                        Status = TapeArbStatus.Active,
                        DateNy = dateNy,
                        MinuteIdx = kv.Value.LastMinuteIdx,

                        Ticker = kv.Key,
                        BenchTicker = kv.Value.BenchTicker,
                        Side = kv.Value.Side,

                        StartDev = kv.Value.StartDev,
                        StartMinuteIdx = kv.Value.StartMinuteIdx,

                        PeakDevAbs = kv.Value.PeakDevAbs,
                        PeakDev = kv.Value.PeakDev,
                        PeakMinuteIdx = kv.Value.PeakMinuteIdx,

                        LastDev = kv.Value.LastDev,

                        Rating = kv.Value.Rating,
                        Total = kv.Value.Total,

                        TierBp = kv.Value.TierBp,
                        Beta = kv.Value.Beta,
                        HedgeNotional = kv.Value.HedgeNotional,

                        StockEntryPct = kv.Value.StockEntryPct,
                        BenchEntryPct = kv.Value.BenchEntryPct
                    })
                    .ToList();
            }
        }

        public IReadOnlyList<TapeArbClosed> GetClosed(string dateNy)
        {
            dateNy = (dateNy ?? "").Trim();

            if (!_byDateNy.TryGetValue(dateNy, out var b))
                return Array.Empty<TapeArbClosed>();

            lock (b.Gate)
            {
                return b.Closed.ToList();
            }
        }
    }
}
