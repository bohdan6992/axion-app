// TradingBridgeApi\Services\Tape\Strategies\Arbitrage\TapeArbitrageHostedService.cs

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBridgeApi.Services.Tape;
using TradingBridgeApi.Services.Tape.Models;
using TradingBridgeApi.Services.Tape.Strategies.Arbitrage.Models;

namespace TradingBridgeApi.Services.Tape.Strategies.Arbitrage
{
    public sealed class TapeArbitrageHostedService : BackgroundService
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly TapeArbitrageEngine _engine;
        private readonly TapeArbitrageStore _store;
        private readonly TapeQueryService _tape;
        private readonly ILogger<TapeArbitrageHostedService> _log;

        public TapeArbitrageHostedService(
            TapeArbitrageEngine engine,
            TapeArbitrageStore store,
            TapeQueryService tape,
            ILogger<TapeArbitrageHostedService> log)
        {
            _engine = engine;
            _store = store;
            _tape = tape;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("[TapeArbHost] starting");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // rough NY date; tape itself stores dateNy folders
                    var todayNy = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd", Inv);

                    // Use available-days to avoid rebuilding for a day that doesn't exist on disk yet.
                    // If today isn't available yet, fall back to latest available day (if any).
                    var available = _tape.GetAvailableDays();
                    if (available.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    var dateNy = available.Contains(todayNy, StringComparer.OrdinalIgnoreCase)
                        ? todayNy
                        : available[0]; // latest available day (already sorted desc)

                    // Find latest minute file for that day (so we can extract tickers)
                    int latestMinute = -1;
                    for (int m = 1199; m >= 0; m--)
                    {
                        if (_tape.HasMinute(dateNy, m))
                        {
                            latestMinute = m;
                            break;
                        }
                    }

                    // If day exists but minutes not written yet, just wait a bit.
                    if (latestMinute < 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    // Build tickers list from latest existing minute.
                    // This enables rating/total enrichment + (optional) gating via IArbitrageFilesService.
                    string[]? tickers = null;
                    try
                    {
                        var rows = await _tape.QueryAsync(new TapeQueryRequest
                        {
                            DateNy = dateNy,
                            MinuteFrom = latestMinute,
                            MinuteTo = latestMinute,
                            Limit = 0
                        }, stoppingToken);

                        if (rows is { Count: > 0 })
                        {
                            tickers = rows
                                .Select(r => r.Ticker)
                                .Where(t => !string.IsNullOrWhiteSpace(t))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    var p = new TapeArbParams
                    {
                        DateNy = dateNy,
                        Metric = TapeArbMetric.SigmaZap,
                        StartAbs = 1.0,
                        EndAbs = 0.2,

                        // Optional static gating like live-signals (enable when ready):
                        // MinRate = 0.3,
                        // MinTotal = 3,

                        // NOTE: engine uses Tickers to fetch rating/total from best_params
                        Tickers = tickers
                    };

                    var (states, closed, lastMinute) = await _engine.BuildDayAsync(p, stoppingToken);

                    // Persist to in-memory store (Active + Closed for this date)
                    _store.UpsertDay(dateNy, lastMinute, states, closed);

                    // Next tick
                    await Task.Delay(TimeSpan.FromSeconds(55), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[TapeArbHost] loop error");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            _log.LogInformation("[TapeArbHost] stopped");
        }
    }
}
