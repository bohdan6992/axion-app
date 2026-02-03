namespace TradingBridgeApi.Dtos.Tickerdays
{
    public sealed class TickerdaysStatusDto
    {
        public string RequestId { get; set; } = "";
        public int Status { get; set; }
        public double Progress { get; set; }
        public string Message { get; set; } = "";
    }
}
