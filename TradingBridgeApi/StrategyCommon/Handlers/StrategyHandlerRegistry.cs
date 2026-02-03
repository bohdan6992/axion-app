using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBridgeApi.StrategyCommon
{
    public sealed class StrategyHandlerRegistry
    {
        private readonly Dictionary<string, IStrategySignalsHandler> _map;

        public StrategyHandlerRegistry(IEnumerable<IStrategySignalsHandler> handlers)
        {
            _map = new Dictionary<string, IStrategySignalsHandler>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in handlers)
            {
                // hard guard: never store null handlers
                if (h is null) continue;

                var key = (h.Strategy ?? string.Empty).Trim().ToLowerInvariant();
                if (key.Length == 0) continue;

                _map[key] = h;
            }
        }

        public bool TryGet(string? strategy, out IStrategySignalsHandler handler)
        {
            var key = (strategy ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key))
                key = "arbitrage";

            // 1) direct hit
            if (_map.TryGetValue(key, out var tmp) && tmp is not null)
            {
                handler = tmp;
                return true;
            }

            // 2) fallback to arbitrage
            if (_map.TryGetValue("arbitrage", out tmp) && tmp is not null)
            {
                handler = tmp;
                return true;
            }

            // 3) last resort: first available handler
            tmp = _map.Values.FirstOrDefault();
            if (tmp is not null)
            {
                handler = tmp;
                return true;
            }

            // 4) nothing registered
            handler = default!;
            return false;
        }

        public IReadOnlyCollection<string> Strategies => _map.Keys.ToArray();
    }
}
