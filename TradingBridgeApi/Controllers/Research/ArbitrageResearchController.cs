using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using TradingBridgeApi.Services.Research;

namespace TradingBridgeApi.Controllers.Research
{
    [ApiController]
    public class ArbitrageResearchController : ControllerBase
    {
        private readonly ArbitragePnlService _svc;
        public ArbitrageResearchController(ArbitragePnlService svc) => _svc = svc;

        [HttpGet("api/research/arbitrage/pnl")]
        public async Task<IActionResult> GetPnl([FromQuery] ArbitragePnlService.Query req)
        {
            var res = await _svc.QueryAsync(req);
            return Ok(new { count = res.Count, items = res });
        }
    }
}
