using System;
using System.Threading;

namespace TradingBridgeApi.Services.Tickerdays
{
    public enum TickerdaysJobStatus
    {
        Running = 2,
        Done = 3,
        Error = 4,
        Cancelled = 5
    }

    public sealed class TickerdaysJobState
    {
        public string RequestId { get; init; } = "";
        public string RequestHash { get; init; } = "";
        public TickerdaysJobStatus Status { get; set; } = TickerdaysJobStatus.Running;

        public double Progress { get; set; } = 0.0;
        public string Message { get; set; } = "";

        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public object? Result { get; set; } // store TickerdaysResultDto
        public string? Error { get; set; }

        public CancellationTokenSource Cts { get; init; } = new();
    }
}
