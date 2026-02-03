using System;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBridgeApi.Services.Tape
{
    public enum TapeWriteStatus
    {
        Written = 1,
        Skipped = 2,
        Failed = 3
    }

    public sealed record TapeWriteResult(
        TapeWriteStatus Status,
        string Reason,
        DateTime UtcMinute,
        string DateNy,
        int MinuteIdx,
        int RowsWritten);

    public interface ITapeWriter
    {
        /// <summary>
        /// Writes tape rows for the minute containing utcNow (rounded down to minute).
        /// Writer is responsible for:
        /// - NY time window gating (00:00..19:59 NY only)
        /// - band / minuteIdx calculation
        /// - 2-day retention (today + yesterday)
        /// - storage format (Parquet+Snappy)
        /// - dedupe/idempotency per minute
        /// </summary>
        Task<TapeWriteResult> WriteMinuteAsync(DateTime utcNow, CancellationToken ct = default);
    }
}
