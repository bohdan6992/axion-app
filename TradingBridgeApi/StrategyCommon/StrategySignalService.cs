using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon;

public sealed class StrategySignalService
{
    private readonly StrategyHandlerRegistry _reg;

    public StrategySignalService(StrategyHandlerRegistry reg)
    {
        _reg = reg;
    }

    // Universal entrypoint for controllers
    public Task<SignalsResponseDto> GetSignalsAsync(SignalsQueryDto q, CancellationToken ct)
    {
        var strategy = (q.Strategy ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(strategy))
            strategy = "arbitrage";

        Console.WriteLine($"[SIG] GetSignalsAsync strategy='{strategy}'");

        if (_reg.TryGet(strategy, out var handler))
            return handler.GetSignalsAsync(q, ct);

        Console.WriteLine($"[SIG] strategy '{strategy}' not wired yet -> empty response");

        return Task.FromResult(new SignalsResponseDto
        {
            Strategy = strategy,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UniverseTickers = 0,
            ReturnedTickers = 0,
            Items = new List<SignalItemDto>()
        });
    }
}
