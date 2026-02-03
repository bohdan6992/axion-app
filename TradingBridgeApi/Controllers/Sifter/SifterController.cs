using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Dtos.Sifter;
using TradingBridgeApi.Services.Sifter;

namespace TradingBridgeApi.Controllers.Sifter;

[ApiController]
[Route("api/sifter")]
public sealed class SifterController : ControllerBase
{
    private readonly SifterDaysService _days;
    private readonly SifterWindowService _window;

    public SifterController(SifterDaysService days, SifterWindowService window)
    {
        _days = days;
        _window = window;
    }

    [HttpPost("days")]
    public async Task<ActionResult<object>> Days([FromBody] SifterDaysRequestDto req, CancellationToken ct)
        => Ok(new { rows = await _days.GetDaysAsync(req, ct) });

    [HttpPost("window")]
    public async Task<ActionResult<SifterWindowResponseDto>> Window([FromBody] SifterWindowRequestDto req, CancellationToken ct)
        => Ok(await _window.RunAsync(req, ct));
}
