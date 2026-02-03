using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Tape;

namespace TradingBridgeApi.Controllers.Tape
{
    [ApiController]
    [Route("api/tape")]
    public sealed class TapeController : ControllerBase
    {
        private readonly TapeQueryService _q;

        public TapeController(TapeQueryService q)
        {
            _q = q;
        }

        [HttpGet("available-days")]
        public async Task<IActionResult> AvailableDays(CancellationToken ct)
        {
            var days = await _q.GetAvailableDaysAsync(ct);
            return Ok(new { ok = true, days });
        }

        [HttpGet("available-nonempty-days")]
        public async Task<IActionResult> AvailableNonEmptyDays(CancellationToken ct)
        {
            var days = await _q.GetAvailableNonEmptyDaysAsync(ct);
            return Ok(new { ok = true, days });
        }

        /// <summary>
        /// Get all rows for a single minute.
        /// Example: GET /api/tape/minute?dateNy=2026-01-21&minuteIdx=570
        /// </summary>
        [HttpGet("minute")]
        public async Task<IActionResult> Minute([FromQuery] string? dateNy, [FromQuery] int? minuteIdx, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dateNy))
                return BadRequest(new { ok = false, error = "dateNy is required (format yyyy-MM-dd)" });

            if (!minuteIdx.HasValue)
                return BadRequest(new { ok = false, error = "minuteIdx is required" });

            var idx = minuteIdx.Value;
            if (idx < 0 || idx > 1199)
                return BadRequest(new { ok = false, error = "minuteIdx out of range (0..1199)" });

            if (!_q.HasMinute(dateNy.Trim(), idx))
                return NotFound(new { ok = false, error = "minute not found" });

            var req = new TapeQueryRequest
            {
                DateNy = dateNy.Trim(),
                MinuteFrom = idx,
                MinuteTo = idx,
                Limit = 0
            };

            var rows = await _q.QueryAsync(req, ct);
            return Ok(new { ok = true, rows });
        }

        /// <summary>
        /// Flexible query over tape. Accepts TapeQueryRequest in body.
        /// </summary>
        [HttpPost("query")]
        public async Task<IActionResult> Query([FromBody] TapeQueryRequest? req, CancellationToken ct = default)
        {
            if (req == null)
                return BadRequest(new { ok = false, error = "request body required" });

            if (string.IsNullOrWhiteSpace(req.DateNy))
                return BadRequest(new { ok = false, error = "DateNy is required in request" });

            var rows = await _q.QueryAsync(req, ct);
            return Ok(new { ok = true, rows });
        }
    }
}
