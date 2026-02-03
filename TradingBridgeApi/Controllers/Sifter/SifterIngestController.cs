using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Sifter;

namespace TradingBridgeApi.Controllers.Sifter;

[ApiController]
[Route("api/sifter/ingest")]
public sealed class SifterIngestController : ControllerBase
{
    private readonly SifterOsbIngestService _svc;

    public SifterIngestController(SifterOsbIngestService svc)
    {
        _svc = svc;
    }

    public sealed class Req
    {
        public string DateNy { get; set; } = "";          // "2026-01-31"
        public List<string>? Tickers { get; set; }        // optional
    }

    [HttpPost("day")]
    public async Task<IActionResult> IngestDay([FromBody] Req req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DateNy) || !DateOnly.TryParse(req.DateNy, out var d))
            return BadRequest(new { ok = false, error = "Invalid DateNy. Expected YYYY-MM-DD." });

        var res = await _svc.IngestDayAsync(d, req.Tickers, ct);
        return Ok(res);
    }
}
