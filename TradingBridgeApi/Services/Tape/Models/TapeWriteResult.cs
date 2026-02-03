// Services/Tape/Models/TapeWriteResult.cs
using System;

namespace TradingBridgeApi.Services.Tape.Models
{
    public sealed record TapeWriteResult
    {
        public bool Success { get; init; }
        public int RowsWritten { get; init; }
        public string? FilePath { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
