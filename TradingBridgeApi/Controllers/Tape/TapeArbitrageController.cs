using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Services.Tape;
using TradingBridgeApi.Services.Tape.Strategies.Arbitrage;
using TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models;

namespace TradingBridgeApi.Controllers.Tape
{
    [ApiController]
    [Route("api/tape/arbitrage")]
    public sealed class TapeArbitrageController : ControllerBase
    {
        private readonly TapeArbitrageStore _store;
        private readonly TapeArbitrageEngine _engine;
        private readonly TapeQueryService _tape;

        public TapeArbitrageController(
            TapeArbitrageStore store,
            TapeArbitrageEngine engine,
            TapeQueryService tape)
        {
            _store = store;
            _engine = engine;
            _tape = tape;
        }

        private static string NormDate(string? s)
            => (s ?? "").Trim();

        private async Task EnsureBuiltAsync(string dateNy, CancellationToken ct)
        {
            var snap = _store.GetSnapshot(dateNy);
            if (snap != null && snap.LastMinuteIdx >= 0)
                return;

            int last = -1;
            for (int m = 1199; m >= 0; m--)
            {
                if (_tape.HasMinute(dateNy, m)) { last = m; break; }
            }

            if (last < 0)
            {
                _store.UpsertDay(dateNy, -1,
                    new Dictionary<string, TapeArbState>(StringComparer.OrdinalIgnoreCase),
                    new List<TapeArbClosed>());
                return;
            }

            var p = new TapeArbParams
            {
                DateNy = dateNy,
                Metric = TapeArbMetric.SigmaZap,

                // Canon base floors (frontend may only increase)
                StartAbs = 0.10,
                EndAbs = 0.05,

                Tickers = null
            };

            // Clamp (future-proof if query params are added later)
            p.StartAbs = Math.Max(p.StartAbs, 0.10);
            p.EndAbs = Math.Max(p.EndAbs, 0.05);

            var (states, closed, lastMinute) = await _engine.BuildDayAsync(p, ct);
            _store.UpsertDay(dateNy, lastMinute, states, closed);
        }

        [HttpGet("snapshot")]
        public async Task<IActionResult> GetSnapshot([FromQuery] string dateNy, CancellationToken ct)
        {
            dateNy = NormDate(dateNy);
            await EnsureBuiltAsync(dateNy, ct);

            var s = _store.GetSnapshot(dateNy);
            if (s == null)
                return Ok(new { ok = true, dateNy, lastMinuteIdx = -1, activeCount = 0, closedCount = 0 });

            return Ok(new
            {
                ok = true,
                dateNy,
                lastMinuteIdx = s.LastMinuteIdx,
                activeCount = s.ActiveCount,
                closedCount = s.ClosedCount,
                updatedUtc = s.UpdatedUtc
            });
        }

        [HttpGet("active")]
        public async Task<IActionResult> GetActive([FromQuery] string dateNy, CancellationToken ct)
        {
            dateNy = NormDate(dateNy);
            await EnsureBuiltAsync(dateNy, ct);

            return Ok(new { ok = true, dateNy, rows = _store.GetActive(dateNy) });
        }

        [HttpGet("closed")]
        public async Task<IActionResult> GetClosed([FromQuery] string dateNy, CancellationToken ct)
        {
            dateNy = NormDate(dateNy);
            await EnsureBuiltAsync(dateNy, ct);

            return Ok(new { ok = true, dateNy, rows = _store.GetClosed(dateNy) });
        }
    }
}
