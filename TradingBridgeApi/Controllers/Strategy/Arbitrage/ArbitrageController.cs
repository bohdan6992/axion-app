// Controllers/Strategy/Arbitrage/ArbitrageController.cs
using Microsoft.AspNetCore.Mvc;

using TradingBridgeApi.Services.Strategy.Arbitrage;
using TradingBridgeApi.StrategyCommon;
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi;

[ApiController]
[Route("/api/strategy/arbitrage")]
public sealed class ArbitrageController : ControllerBase
{
    // =========================
    // SUMMARY
    // =========================
    // GET /api/strategy/arbitrage/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? q,
        [FromServices] ArbitrageFilesService files,
        CancellationToken ct)
    {
        var resp = await files.GetSummaryAsync(q, ct);

        return Ok(new
        {
            ok = true,
            format = "csv",
            updatedAt = resp.UpdatedAtUtc,
            count = resp.Count,
            header = resp.Header,
            items = resp.Items
        });
    }

    // =========================
    // TICKER (onefile)
    // =========================
    // GET /api/strategy/arbitrage/ticker/{ticker}
    [HttpGet("ticker/{ticker}")]
    public async Task<IActionResult> GetTicker(
        string ticker,
        [FromServices] ArbitrageFilesService files,
        CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker))
            return BadRequest(new { ok = false, error = "ticker is required", type = "TICKER_REQUIRED" });

        var el = await files.GetOnefileTickerAsync(ticker, ct);
        if (!el.HasValue)
            return NotFound(new { ok = false, error = $"ticker '{ticker}' not found", type = "TICKER_NOT_FOUND", ticker });

        return Ok(new
        {
            ok = true,
            format = "jsonl",
            updatedAt = files.OnefileUpdatedAtUtc,
            ticker,
            item = el.Value
        });
    }

    // =========================
    // BEST PARAMS
    // =========================
    // GET /api/strategy/arbitrage/best-params/{ticker}
    [HttpGet("best-params/{ticker}")]
    public async Task<IActionResult> GetBestParams(
        string ticker,
        [FromServices] ArbitrageFilesService files,
        CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker))
            return BadRequest(new { ok = false, error = "ticker is required", type = "TICKER_REQUIRED" });

        var el = await files.GetBestParamsTickerAsync(ticker, ct);
        if (!el.HasValue)
            return NotFound(new { ok = false, error = $"ticker '{ticker}' not found", type = "TICKER_NOT_FOUND", ticker });

        return Ok(new
        {
            ok = true,
            format = "jsonl",
            updatedAt = files.BestParamsUpdatedAtUtc,
            ticker,
            item = el.Value
        });
    }

    // =========================
    // SIGNALS (canonical)
    // =========================
    // GET /api/strategy/arbitrage/signals?class=print&type=any&mode=top&tickers=AAPL,MSFT&limit=50&offset=0&minRate=0.3&minTotal=3
    [HttpGet("signals")]
    public async Task<IActionResult> GetSignals(
        [FromQuery(Name = "class")] string? cls,
        [FromQuery] string? type,
        [FromQuery] string? mode,
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
            Strategy = "arbitrage",
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
