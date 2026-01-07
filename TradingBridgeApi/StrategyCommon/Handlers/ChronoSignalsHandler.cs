using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Handlers;

public sealed class ChronoSignalsHandler : IStrategySignalsHandler
{
    public string Strategy => "chrono";

    public Task<SignalsResponseDto> GetSignalsAsync(SignalsQueryDto q, CancellationToken ct)
    {
        // Minimal valid response, so API doesn't 500 and UI can render "empty".
        var resp = new SignalsResponseDto
        {
            Strategy = Strategy,
            GeneratedAt = DateTimeOffset.UtcNow,
            UniverseTickers = 0,
            ReturnedTickers = 0,
            Items = new List<SignalItemDto>()
        };

        return Task.FromResult(resp);
    }
}
