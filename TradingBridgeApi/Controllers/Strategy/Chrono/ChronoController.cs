using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Strategy.Chrono;

namespace TradingBridgeApi.Controllers.Strategy.Chrono;

[ApiController]
[Route("api/strategy/chrono")]
public sealed class ChronoController : ControllerBase
{
    private readonly ChronoFilesService _files;

    public ChronoController(ChronoFilesService files)
    {
        _files = files;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetSummaryAsync(ct);
        if (!ok) return BadRequest(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    [HttpGet("ticker/{ticker}")]
    public async Task<IActionResult> Ticker([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetTickerAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    [HttpGet("best-params/{ticker}")]
    public async Task<IActionResult> BestParams([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetBestParamsAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }
}
