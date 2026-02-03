using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TradingBridgeApi.Dtos.Tickerdays;
using TradingBridgeApi.Options;
using TradingBridgeApi.Services.Tape;

namespace TradingBridgeApi.Services.Tickerdays
{
    public sealed class TickerdaysReportBuilder
    {
        private readonly TapeQueryService _tape;
        private readonly TickerdaysOptions _opt;

        public TickerdaysReportBuilder(TapeQueryService tape, IOptions<TickerdaysOptions> opt)
        {
            _tape = tape;
            _opt = opt.Value;
        }

        public static string ComputeRequestHash(TickerdaysReportRequestDto req)
        {
            // normalize tickers (upper+unique+sorted) for stable hash
            var tickers = (req.Tickers ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Tickerdays operates on NY trading dates (dateNy), not instants.
            // Prefer explicit StartDateNy/EndDateNy; fallback to StartDate.Date/EndDate.Date (no UTC conversion).
            var (startNy, endNy) = GetNyDateRange(req);

            var normalized = new
            {
                startNy = startNy.ToString("yyyy-MM-dd"),
                endNy = endNy.ToString("yyyy-MM-dd"),
                tickers,
                fetchDataMode = req.FetchDataMode,
                filters = req.Filters, // deterministic enough for MVP
                addPrice = req.AdditionalPriceData,
                addVol = req.AdditionalVolumeData,
                addPriceParams = req.AdditionalPriceDataWithParams
            };

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var bytes = Encoding.UTF8.GetBytes(json);
            var hash = SHA1.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public async Task<TickerdaysResultDto> BuildAsync(
            TickerdaysReportRequestDto req,
            Action<double, string>? progress,
            CancellationToken ct)
        {
            var tickers = (req.Tickers ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var (startNy, endNy) = GetNyDateRange(req);
            if (endNy < startNy) (startNy, endNy) = (endNy, startNy);

            // Only days that exist in tape
            var available = _tape.GetAvailableNonEmptyDays(ct)
                .Select(x => x.Trim())
                .Where(x => x.Length == 10)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var allDays = new List<string>();
            for (var d = startNy; d <= endNy; d = d.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                var dateNy = d.ToString("yyyy-MM-dd");
                if (available.Contains(dateNy))
                    allDays.Add(dateNy);
            }

            var result = new TickerdaysResultDto
            {
                Meta = new TickerdaysMetaDto
                {
                    StartDateNy = startNy.ToString("yyyy-MM-dd"),
                    EndDateNy = endNy.ToString("yyyy-MM-dd"),
                    Tickers = tickers,
                    FetchDataMode = req.FetchDataMode
                }
            };

            var priceFilters = req.Filters?.PricePercFilters ?? new List<PricePercFilterDto>();
            // MVP: ignore unsupported dayIndex != 0 by treating them as "not passing"
            // (keeps semantics strict and predictable)

            // Precompute window ranges for each filter
            var filterWindows = priceFilters.Select(f => ResolveMinuteRange(f.TimeStart, f.TimeEnd)).ToList();

            int processed = 0;
            int total = Math.Max(1, allDays.Count);

            foreach (var dateNy in allDays)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((double)processed / total, $"Scanning {dateNy}…");

                // For this day, we need data per filter window to compute pct.
                // We'll compute per filter: pctByTicker[filterIdx][ticker] = pct
                var pctByFilter = new List<Dictionary<string, double>>(capacity: priceFilters.Count);
                var metaByTicker = new Dictionary<string, TapeRowMeta>(StringComparer.OrdinalIgnoreCase);

                for (int fi = 0; fi < priceFilters.Count; fi++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (mf, mt) = filterWindows[fi];
                    var tapeReq = new TapeQueryRequest
                    {
                        DateNy = dateNy,
                        MinuteFrom = mf,
                        MinuteTo = mt,
                        Tickers = tickers.ToArray(),
                        Limit = 0
                    };

                    var rows = await _tape.QueryAsync(tapeReq, ct);

                    var grp = rows
                        .Where(r => !string.IsNullOrWhiteSpace(r.Ticker))
                        .GroupBy(r => r.Ticker.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

                    var pctMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                    foreach (var g in grp)
                    {
                        var ordered = g.OrderBy(x => x.MinuteIdx).ToList();

                        // meta snapshot
                        if (!metaByTicker.ContainsKey(g.Key))
                        {
                            var metaRow = ordered.FirstOrDefault(x =>
                                            x.MarketCapM.HasValue ||
                                            !string.IsNullOrWhiteSpace(x.SectorL3) ||
                                            !string.IsNullOrWhiteSpace(x.Exchange) ||
                                            x.GapPct.HasValue ||
                                            x.ClsToClsPct.HasValue ||
                                            x.YCls.HasValue ||
                                            x.LstCls.HasValue ||
                                            x.LstPrcLstClsPct.HasValue ||
                                            x.LstPrcTOpenPct.HasValue ||
                                            x.TOpen.HasValue ||
                                            x.TCls.HasValue ||
                                            x.ATR14.HasValue ||
                                            x.Hi.HasValue)
                                       ?? ordered.FirstOrDefault();

                            if (metaRow != null)
                            {
                                metaByTicker[g.Key] = new TapeRowMeta
                                {
                                    MarketCapM = metaRow.MarketCapM,
                                    SectorL3 = metaRow.SectorL3,
                                    Exchange = metaRow.Exchange,
                                    Adv20 = metaRow.Adv20,
                                    GapPct = metaRow.GapPct,
                                    ClsToClsPct = metaRow.ClsToClsPct,

                                    YCls = metaRow.YCls,
                                    LstCls = metaRow.LstCls,
                                    LstPrcLstClsPct = metaRow.LstPrcLstClsPct,
                                    LstPrcTOpenPct = metaRow.LstPrcTOpenPct,
                                    TOpen = metaRow.TOpen,
                                    TCls = metaRow.TCls,
                                    ATR14 = metaRow.ATR14,
                                    Hi = metaRow.Hi
                                };
                            }
                        }

                        var start = ordered.FirstOrDefault(x => x.Mid.HasValue && x.Mid.Value > 0)?.Mid;
                        var end = ordered.LastOrDefault(x => x.Mid.HasValue && x.Mid.Value > 0)?.Mid;

                        if (!start.HasValue || !end.HasValue) continue;

                        var pct = (end.Value / start.Value - 1.0) * 100.0;
                        pctMap[g.Key] = pct;
                    }

                    pctByFilter.Add(pctMap);
                }

                var dayRows = new List<TickerdaysDayRowDto>();

                foreach (var ticker in tickers)
                {
                    ct.ThrowIfCancellationRequested();

                    var tags = new List<string>();
                    bool okAll = true;

                    for (int fi = 0; fi < priceFilters.Count; fi++)
                    {
                        var f = priceFilters[fi];

                        if (f.DayIndex != 0)
                        {
                            okAll = false;
                            break;
                        }

                        if (!pctByFilter[fi].TryGetValue(ticker, out var pct))
                        {
                            okAll = false;
                            break;
                        }

                        if (!PassPriceFilter(f, pct))
                        {
                            okAll = false;
                            break;
                        }

                        tags.Add($"pass_pricePercFilter_{fi}");
                    }

                    if (!okAll) continue;

                    double? pctChange = null;
                    if (priceFilters.Count > 0 && pctByFilter[0].TryGetValue(ticker, out var p0))
                        pctChange = p0;

                    metaByTicker.TryGetValue(ticker, out var meta);

                    dayRows.Add(new TickerdaysDayRowDto
                    {
                        Ticker = ticker,
                        DateNy = dateNy,
                        PctChange = pctChange,
                        Tags = tags,

                        MarketCapM = meta?.MarketCapM,
                        SectorL3 = meta?.SectorL3,
                        Exchange = meta?.Exchange,
                        Adv20 = meta?.Adv20,
                        GapPct = meta?.GapPct,
                        ClsToClsPct = meta?.ClsToClsPct
                    });
                }

                result.Days.AddRange(dayRows);
                processed++;
            }

            progress?.Invoke(0.95, "Building performance…");

            result.Performance.Trades = result.Days
                .Select(d => new TickerdaysTradeDto
                {
                    Ticker = d.Ticker,
                    DateNy = d.DateNy,
                    PnlPct = d.PctChange,
                    Tags = d.Tags.ToList()
                })
                .ToList();

            result.Performance.Summary = result.Days
                .GroupBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var vals = g.Select(x => x.PctChange).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                    var days = vals.Count;
                    var win = days == 0 ? 0 : vals.Count(v => v > 0) / (double)days;
                    var avg = days == 0 ? 0 : vals.Average();
                    var med = Median(vals);

                    return new TickerdaysTickerSummaryDto
                    {
                        Ticker = g.Key,
                        Days = days,
                        WinRate = win,
                        Avg = avg,
                        Median = med
                    };
                })
                .OrderByDescending(x => x.Days)
                .ToList();

            if (req.FetchDataMode >= 2)
            {
                progress?.Invoke(0.98, "Loading intraday for matched days…");

                var cap = Math.Max(1, _opt.Intraday?.MaxTickerDays ?? 200);

                var keys = result.Days
                    .Select(d => (d.Ticker, d.DateNy))
                    .Distinct()
                    .Take(cap)
                    .ToList();

                var dayWindow = _opt.Windows.FirstOrDefault(w => w.Id == 4);
                var from = dayWindow?.MinuteFrom ?? 0;
                var to = dayWindow?.MinuteTo ?? 390;

                foreach (var (ticker, dateNy) in keys)
                {
                    ct.ThrowIfCancellationRequested();

                    var rows = await _tape.QueryAsync(new TapeQueryRequest
                    {
                        DateNy = dateNy,
                        MinuteFrom = from,
                        MinuteTo = to,
                        Tickers = new[] { ticker },
                        Limit = 0
                    }, ct);

                    var series = rows
                        .OrderBy(x => x.MinuteIdx)
                        .Select(x => new TickerdaysIntradayPointDto
                        {
                            T = x.MinuteNy ?? "",
                            C = x.Mid ?? x.Bid ?? x.Ask,
                            V = x.Vol
                        })
                        .ToList();

                    result.Intraday[$"{ticker}|{dateNy}"] = series;
                }
            }

            progress?.Invoke(1.0, "Done");
            return result;
        }

        private (int from, int to) ResolveMinuteRange(int timeStartId, int timeEndId)
        {
            var wStart = _opt.Windows.FirstOrDefault(w => w.Id == timeStartId);
            var wEnd = _opt.Windows.FirstOrDefault(w => w.Id == timeEndId);

            int from = wStart?.MinuteFrom ?? 0;
            int to = wEnd?.MinuteTo ?? 30;

            if (from < 0) from = 0;
            if (to > 1199) to = 1199;
            if (to < from) (from, to) = (to, from);

            return (from, to);
        }

        private static bool PassPriceFilter(PricePercFilterDto f, double pct)
        {
            var thr = Math.Abs(f.PricePercChange);

            if (f.IsAbsChange)
                return Math.Abs(pct) >= thr;

            return f.Side switch
            {
                1 => pct >= thr,         // pos
                2 => pct <= -thr,        // neg
                _ => Math.Abs(pct) >= thr // any
            };
        }

        private static double Median(List<double> vals)
        {
            if (vals.Count == 0) return 0;
            vals.Sort();
            int mid = vals.Count / 2;
            if (vals.Count % 2 == 1) return vals[mid];
            return (vals[mid - 1] + vals[mid]) / 2.0;
        }

        private static (DateTime startNy, DateTime endNy) GetNyDateRange(TickerdaysReportRequestDto req)
        {
            // Preferred: explicit dateNy strings in "yyyy-MM-dd"
            if (TryParseDateNy(req.StartDateNy, out var s) && TryParseDateNy(req.EndDateNy, out var e))
                return (s, e);

            // Fallback: use Date part as provided (no UTC conversion)
            return (req.StartDate.Date, req.EndDate.Date);
        }

        private static bool TryParseDateNy(string? s, out DateTime d)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                d = default;
                return false;
            }

            return DateTime.TryParseExact(
                s.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out d);
        }

        private sealed class TapeRowMeta
        {
            public double? MarketCapM { get; set; }
            public string? SectorL3 { get; set; }
            public string? Exchange { get; set; }
            public double? Adv20 { get; set; }
            public double? GapPct { get; set; }
            public double? ClsToClsPct { get; set; }

            public double? YCls { get; set; }
            public double? LstCls { get; set; }
            public double? LstPrcLstClsPct { get; set; }
            public double? LstPrcTOpenPct { get; set; }
            public double? TOpen { get; set; }
            public double? TCls { get; set; }
            public double? ATR14 { get; set; }
            public double? Hi { get; set; }
        }
    }
}
