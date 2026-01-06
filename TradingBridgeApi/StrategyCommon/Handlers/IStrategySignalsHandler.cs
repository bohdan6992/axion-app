using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon;

public interface IStrategySignalsHandler
{
    string Strategy { get; }   // e.g. "arbitrage"
    Task<SignalsResponseDto> GetSignalsAsync(SignalsQueryDto q, CancellationToken ct);
}
