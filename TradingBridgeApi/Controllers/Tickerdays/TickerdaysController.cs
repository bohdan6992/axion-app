using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TradingBridgeApi.Dtos.Tickerdays;
using TradingBridgeApi.Services.Tickerdays;

namespace TradingBridgeApi.Controllers.Tickerdays
{
    [ApiController]
    [Route("api/tickerdays")]
    public sealed class TickerdaysController : ControllerBase
    {
        private readonly TickerdaysJobStore _store;
        private readonly TickerdaysReportBuilder _builder;

        public TickerdaysController(TickerdaysJobStore store, TickerdaysReportBuilder builder)
        {
            _store = store;
            _builder = builder;
        }

        [HttpPost("report")]
        public ActionResult<TickerdaysAckDto> Create([FromBody] TickerdaysReportRequestDto req)
        {
            if (req == null)
                return BadRequest("Empty request");

            if (req.Tickers == null || req.Tickers.Count == 0)
                return BadRequest("tickers[] required");

            // Normalize tickers in-place
            req.Tickers = req.Tickers
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hash = TickerdaysReportBuilder.ComputeRequestHash(req);
            var existing = _store.TryGetByHash(hash);

            if (existing != null)
            {
                // Cache hit: if Done -> return immediately
                if (existing.Status == TickerdaysJobStatus.Done)
                {
                    return new TickerdaysAckDto
                    {
                        RequestId = existing.RequestId,
                        Status = (int)existing.Status
                    };
                }

                // Replace policy: new run cancels previous job for same payload hash
                // - Running: cancel and replace
                // - Error/Cancelled: replace (do not reuse)
                _store.Cancel(existing.RequestId, "Replaced by new request");
            }

            var job = _store.CreateNew(hash);

            _ = Task.Run(async () =>
            {
                try
                {
                    _store.UpdateProgress(job.RequestId, 0.0, "Starting…");

                    var res = await _builder.BuildAsync(
                        req,
                        (p, m) => _store.UpdateProgress(job.RequestId, p, m),
                        job.Cts.Token);

                    _store.Complete(job.RequestId, res);
                }
                catch (OperationCanceledException)
                {
                    _store.Cancel(job.RequestId, "Cancelled");
                }
                catch (Exception ex)
                {
                    _store.Fail(job.RequestId, ex.Message);
                }
            });

            return new TickerdaysAckDto
            {
                RequestId = job.RequestId,
                Status = (int)job.Status
            };
        }

        [HttpGet("status/{requestId}")]
        public ActionResult<TickerdaysStatusDto> Status([FromRoute] string requestId)
        {
            var j = _store.Get(requestId);
            if (j == null) return NotFound();

            return new TickerdaysStatusDto
            {
                RequestId = j.RequestId,
                Status = (int)j.Status,
                Progress = j.Progress,
                Message = j.Message
            };
        }

        [HttpGet("result/{requestId}")]
        public ActionResult Result([FromRoute] string requestId)
        {
            var j = _store.Get(requestId);
            if (j == null) return NotFound();

            if (j.Status == TickerdaysJobStatus.Done)
                return Ok(j.Result);

            if (j.Status == TickerdaysJobStatus.Error)
                return Problem(j.Error ?? "Unknown error");

            if (j.Status == TickerdaysJobStatus.Cancelled)
                return Conflict(new { requestId, status = (int)j.Status, message = j.Message });

            return Conflict(new { requestId, status = (int)j.Status, message = "Not ready" });
        }

        [HttpPost("cancel/{requestId}")]
        public ActionResult Cancel([FromRoute] string requestId)
        {
            var j = _store.Get(requestId);
            if (j == null) return NotFound();

            _store.Cancel(requestId, "Cancelled");
            return Ok(new { requestId, status = (int)TickerdaysJobStatus.Cancelled });
        }
    }
}
