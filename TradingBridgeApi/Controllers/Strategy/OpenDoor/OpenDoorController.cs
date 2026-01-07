using Microsoft.AspNetCore.Mvc;

using TradingBridgeApi.Services.Strategy.OpenDoor;
using TradingBridgeApi.StrategyCommon.Dtos;
using TradingBridgeApi.StrategyCommon.Signals;

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

    // =========================
    // SUMMARY (normalized for Matrix UI)
    // =========================
    // GET /api/opendoor/summary?q=AAPL
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? q, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetSummaryAsync(ct);
        if (!ok) return BadRequest(new { ok, error = err, errorType = errType });

        var (updatedAt, csv) = SummaryPayloadExtractor.Extract(payload);
        var (header, items) = CsvMini.Parse(csv, q);

        return Ok(new
        {
            ok = true,
            strategy = "opendoor",
            format = "csv",
            updatedAt,
            count = items.Count,
            header,
            items
        });
    }

    // =========================
    // TICKER
    // =========================
    // GET /api/opendoor/ticker/{ticker}
    [HttpGet("ticker/{ticker}")]
    public async Task<IActionResult> Ticker([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetTickerAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    // =========================
    // BEST PARAMS
    // =========================
    // GET /api/opendoor/best-params/{ticker}
    [HttpGet("best-params/{ticker}")]
    public async Task<IActionResult> BestParams([FromRoute] string ticker, CancellationToken ct)
    {
        var (ok, err, errType, payload) = await _files.GetBestParamsAsync(ticker, ct);
        if (!ok) return NotFound(new { ok, error = err, errorType = errType });
        return Ok(payload);
    }

    // =========================
    // SIGNALS
    // =========================
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
        var qdto = new SignalsQueryDto
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

        var resp = await signals.GetSignalsAsync(qdto, ct);

        return Ok(new
        {
            ok = true,
            strategy = resp.Strategy,
            generatedAt = resp.GeneratedAt,
            universeTickers = resp.UniverseTickers,
            returned = resp.ReturnedTickers,
            cls = qdto.Class,
            type = qdto.Type,
            mode = qdto.Mode,
            items = resp.Items
        });
    }

    // =========================
    // Helpers (local to controller)
    // =========================

    private static class SummaryPayloadExtractor
    {
        public static (object? updatedAt, string csv) Extract(object payload)
        {
            if (payload is null) return (null, "");

            // if service returns raw csv string
            if (payload is string s)
                return (null, s);

            // if service returns { updatedAt, text } (or similar)
            var t = payload.GetType();

            var updatedAtProp =
                t.GetProperty("updatedAt") ??
                t.GetProperty("UpdatedAt") ??
                t.GetProperty("updatedAtUtc") ??
                t.GetProperty("UpdatedAtUtc");

            var textProp =
                t.GetProperty("text") ??
                t.GetProperty("Text") ??
                t.GetProperty("csv") ??
                t.GetProperty("Csv");

            var updatedAt = updatedAtProp?.GetValue(payload);
            var csv = (string?)textProp?.GetValue(payload) ?? payload.ToString() ?? "";

            return (updatedAt, csv);
        }
    }

    private static class CsvMini
    {
        public static (List<string> Header, List<Dictionary<string, string?>> Items) Parse(string csv, string? q)
        {
            q = (q ?? "").Trim();

            if (string.IsNullOrWhiteSpace(csv))
                return (new List<string>(), new List<Dictionary<string, string?>>());

            var lines = csv.Replace("\r\n", "\n").Replace("\r", "\n")
                           .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return (new List<string>(), new List<Dictionary<string, string?>>());

            var header = Split(lines[0]);
            var items = new List<Dictionary<string, string?>>(Math.Max(0, lines.Length - 1));

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = Split(lines[i]);
                if (cols.Count == 0) continue;

                // filter by ticker (first column) if q provided
                if (!string.IsNullOrEmpty(q))
                {
                    var ticker = cols.Count > 0 ? cols[0] : "";
                    if (ticker is null || !ticker.Contains(q, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count; c++)
                {
                    var key = header[c];
                    var val = c < cols.Count ? cols[c] : null;
                    row[key] = string.IsNullOrWhiteSpace(val) ? null : val;
                }

                items.Add(row);
            }

            return (header, items);
        }

        private static List<string> Split(string line)
            => line.Split(',').Select(x => x.Trim()).ToList();
    }
}
