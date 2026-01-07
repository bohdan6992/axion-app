using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Handlers;

public sealed class OpenDoorSignalsHandler : IStrategySignalsHandler
{
    public string Strategy => "opendoor";

    public Task<SignalsResponseDto> GetSignalsAsync(SignalsQueryDto q, CancellationToken ct)
    {
        var resp = new SignalsResponseDto
        {
            Strategy = Strategy,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // ✅ long
            UniverseTickers = 0,
            ReturnedTickers = 0,
            Items = new List<SignalItemDto>()
        };

        return Task.FromResult(resp);
    }
}
