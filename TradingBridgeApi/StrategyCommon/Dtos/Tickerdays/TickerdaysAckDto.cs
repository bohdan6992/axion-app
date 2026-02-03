namespace TradingBridgeApi.Dtos.Tickerdays
{
    public sealed class TickerdaysAckDto
    {
        public string RequestId { get; set; } = "";
        public int Status { get; set; } = 2; // 2 running, 3 done, 4 error
    }
}
