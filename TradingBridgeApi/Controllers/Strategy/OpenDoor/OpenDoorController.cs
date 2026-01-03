using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Strategy.OpenDoor;

namespace TradingBridgeApi.Controllers.Strategy.OpenDoor;

[ApiController]
[Route("api/strategy/opendoor")]
public sealed class OpenDoorController : ControllerBase
{
    private readonly OpenDoorFilesService _files;

    public OpenDoorController(OpenDoorFilesService files)
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

    // TODO: підключимо Joiner/Eligibility/TopMode як тільки ти додаси StrategyCommon/Signals
    [HttpGet("signals")]
    public IActionResult Signals()
    {
        return Ok(new
        {
            ok = true,
            strategy = "opendoor",
            note = "signals endpoint not wired yet (needs StrategyJoiner + policies)."
        });
    }
}
