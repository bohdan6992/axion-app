namespace TradingBridgeApi.StrategyCommon;

public sealed class StrategyHandlerRegistry
{
    private readonly Dictionary<string, IStrategySignalsHandler> _map;

    public StrategyHandlerRegistry(IEnumerable<IStrategySignalsHandler> handlers)
    {
        _map = new Dictionary<string, IStrategySignalsHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in handlers)
        {
            var key = (h.Strategy ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            _map[key] = h;
        }
    }

    public bool TryGet(string? strategy, out IStrategySignalsHandler handler)
    {
        handler = null!;

        var key = (strategy ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key))
            key = "arbitrage";

        return _map.TryGetValue(key, out handler);
    }

    public IReadOnlyCollection<string> Strategies => _map.Keys.ToArray();
}
