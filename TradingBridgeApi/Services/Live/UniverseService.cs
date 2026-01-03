using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingBridgeApi.Services.Live;

public sealed class UniverseService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<UniverseService> _log;

    public UniverseService(IWebHostEnvironment env, ILogger<UniverseService> log)
    {
        _env = env;
        _log = log;
    }

    private string UniversePath => Path.Combine(_env.ContentRootPath, "universe.csv");

    // ✅ Backward compatible: tickers only
    public async Task<List<string>> LoadUniverseAsync(CancellationToken ct = default)
    {
        var map = await LoadUniverseWithBenchAsync(ct);
        return map.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ✅ New: ticker -> bench (bench може бути "" якщо нема)
    public Task<Dictionary<string, string>> LoadUniverseWithBenchAsync(CancellationToken ct = default)
    {
        var path = UniversePath;
        if (!File.Exists(path))
        {
            _log.LogWarning("Universe file not found: {Path}", path);
            return Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();

            var s = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (s.StartsWith("#")) continue;

            // формат: TICKER[,BENCH]
            var parts = s.Split(',', StringSplitOptions.TrimEntries);

            var ticker = parts.Length >= 1 ? (parts[0] ?? "").Trim().ToUpperInvariant() : "";
            if (string.IsNullOrWhiteSpace(ticker)) continue;

            var bench = parts.Length >= 2 ? (parts[1] ?? "").Trim().ToUpperInvariant() : "";
            dict[ticker] = bench; // якщо дубль — останній рядок перемагає
        }

        return Task.FromResult(dict);
    }
}
