using Microsoft.AspNetCore.Mvc;

using TradingBridgeApi.Services.Strategy.OpenDoor;
using TradingBridgeApi.StrategyCommon.Dtos;
using TradingBridgeApi.StrategyCommon.Signals;
using TradingBridgeApi.StrategyCommon;


namespace TradingBridgeApi.Controllers;

[ApiController]
[Route("/api/opendoor")]
public sealed class OpenDoorController : ControllerBase
{
    private readonly OpenDoorFilesService _files;

    public OpenDoorController(OpenDoorFilesService files)
    {
        _files = files;
    }

    // GET /api/opendoor/summary
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetSummaryAsync(ct);
        if (!ok) return BadRequest(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    // GET /api/opendoor/ticker/{ticker}
    [HttpGet("ticker/{ticker}")]
    public async Task<IActionResult> Ticker([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetTickerAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    // GET /api/opendoor/best-params/{ticker}
    [HttpGet("best-params/{ticker}")]
    public async Task<IActionResult> BestParams([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetBestParamsAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    // ✅ NEW canonical signals route:
    // GET /api/opendoor/signals/{cls}/{type}/{mode}?tickers=AAPL,MSFT&limit=50&offset=0&minRate=0.3&minTotal=3
    [HttpGet("signals/{cls}/{type}/{mode}")]
    public async Task<IActionResult> Signals(
        [FromRoute] string cls,
        [FromRoute] string type,
        [FromRoute] string mode,
        [FromQuery] string? tickers,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromQuery] decimal? minRate,
        [FromQuery] int? minTotal,
        [FromServices] StrategySignalService signals,
        CancellationToken ct)
    {
        var q = new SignalsQueryDto
        {
            Strategy = "opendoor",
            Class = (cls ?? "global").Trim().ToLowerInvariant(),
            Type = (type ?? "any").Trim().ToLowerInvariant(),
            Mode = (mode ?? "all").Trim().ToLowerInvariant(),

            Tickers = tickers,
            Limit = limit,
            Offset = offset,

            MinRate = minRate ?? 0.3m,
            MinTotal = minTotal ?? 3
        };

        var resp = await signals.GetSignalsAsync(q, ct);

        return Ok(new
        {
            ok = true,
            strategy = resp.Strategy,
            generatedAt = resp.GeneratedAt,
            universeTickers = resp.UniverseTickers,
            returned = resp.ReturnedTickers,
            cls = q.Class,
            type = q.Type,
            mode = q.Mode,
            items = resp.Items
        });
    }
}
