using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Live;

namespace TradingBridgeApi.Controllers.Live;

[ApiController]
[Route("api/live")]
public sealed class LiveSnapshotController : ControllerBase
{
    private readonly LiveSnapshotService _live;

    public LiveSnapshotController(LiveSnapshotService live)
    {
        _live = live;
    }

    /// <summary>
    /// Основний live snapshot (використовується joiner-ом як LIVE meta).
    /// GET /api/live/snapshot?tickers=AAPL,MSFT
    /// GET /api/live/snapshot?tickers=AAPL,MSFT&fields=Bid,Ask,VWAP
    /// </summary>
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(
        [FromQuery] string? tickers,
        [FromQuery] string? fields,
        CancellationToken ct)
    {
        var allow = ParseTickersAllow(tickers);

        var (universeCount, items) = await _live.GetSnapshotAsync(
            tickerAllow: allow,
            fieldsCsv: fields,
            ct: ct
        );

        return Ok(new
        {
            ok = true,
            capturedAt = DateTimeOffset.UtcNow,
            universeTickers = universeCount,
            returned = items.Count,
            items
        });
    }

    /// <summary>
    /// Debug endpoint як старий /api/full-quotes:
    /// Дозволяє перевірити, які назви полів DLL реально віддає.
    /// GET /api/live/full-quotes?tickers=A,AA&fields=ADV20,VWAP,VolDay,TCls,ClsToCls%
    /// </summary>
    [HttpGet("full-quotes")]
    public async Task<IActionResult> FullQuotes(
        [FromQuery] string? tickers,
        [FromQuery] string? fields,
        CancellationToken ct)
    {
        var allow = ParseTickersAllow(tickers);
        if (allow is null || allow.Count == 0)
            return BadRequest(new
            {
                ok = false,
                error = "tickers is required for /api/live/full-quotes",
                type = "TICKERS_REQUIRED"
            });

        if (string.IsNullOrWhiteSpace(fields))
            return BadRequest(new
            {
                ok = false,
                error = "fields is required for /api/live/full-quotes",
                type = "FIELDS_REQUIRED"
            });

        // тут universe не потрібен: ми просто просимо snapshot service зробити quotes на allow-list.
        // Щоб не міняти сервіс сильно — просто викликаємо snapshot з fieldsCsv=fields,
        // а universe filter відсіє все крім allow.
        var (universeCount, items) = await _live.GetSnapshotAsync(
            tickerAllow: allow,
            fieldsCsv: fields,
            ct: ct
        );

        return Ok(new
        {
            ok = true,
            capturedAt = DateTimeOffset.UtcNow,
            universeTickers = universeCount,
            requestedTickers = allow.Count,
            returned = items.Count,
            fieldsUsed = fields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            items
        });
    }

    private static HashSet<string>? ParseTickersAllow(string? tickersCsv)
    {
        if (string.IsNullOrWhiteSpace(tickersCsv))
            return null;

        var set = tickersCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return set.Count == 0 ? null : set;
    }
}
