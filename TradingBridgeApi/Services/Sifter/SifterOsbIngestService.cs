using Microsoft.Extensions.Logging;
using TradingBridgeApi.Dtos.Sifter;
using TradingBridgeApi.Services.Live;

namespace TradingBridgeApi.Services.Sifter;

public sealed class SifterOsbIngestService
{
    private readonly LiveSnapshotService _snap;
    private readonly UniverseService _universe;
    private readonly SifterDayOsbStore _store;
    private readonly ILogger<SifterOsbIngestService> _log;

    public SifterOsbIngestService(
        LiveSnapshotService snap,
        UniverseService universe,
        SifterDayOsbStore store,
        ILogger<SifterOsbIngestService> log)
    {
        _snap = snap;
        _universe = universe;
        _store = store;
        _log = log;
    }

    public async Task<object> IngestDayAsync(
        DateOnly dateNy,
        List<string>? tickers,
        CancellationToken ct)
    {
        // ✅ choose tickers
        string[] list;
        if (tickers is { Count: > 0 })
        {
            list = tickers
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct()
                .ToArray();
        }
        else
        {
            list = (await _universe.LoadUniverseAsync(ct)).ToArray();
        }

        if (list.Length == 0)
            return new { ok = true, dateNy = dateNy.ToString("yyyy-MM-dd"), requested = 0, written = 0, note = "no tickers" };

        // ✅ get FULL snapshot so we have OSB fields (Gap/Gap%/ClsToCls%)
        var (_, items) = await _snap.GetSnapshotForTickersAsync(list, fieldsCsv: "FULL", ct: ct);

        var rows = new List<SifterDayRowDto>(capacity: items.Count);

        foreach (var it in items)
        {
            var f = it.Fields;
            if (f is null || f.Count == 0) continue;

            double? GetD(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (f.TryGetValue(k, out var obj) && obj is not null)
                    {
                        if (obj is double d) return d;
                        if (obj is float ff) return ff;
                        if (obj is int i) return i;
                        if (obj is long l) return l;
                        if (obj is string s && double.TryParse(
                            s.Replace("%", "").Replace(",", "."),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsed))
                            return parsed;
                    }
                }
                return null;
            }

            long? GetL(params string[] keys)
            {
                var d = GetD(keys);
                if (d is null) return null;

                // volumes/adv are ints in UI; be conservative
                var v = (long)Math.Round(d.Value, MidpointRounding.AwayFromZero);
                return v < 0 ? null : v;
            }

            string? GetS(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (f.TryGetValue(k, out var obj) && obj is not null)
                    {
                        var s = obj as string ?? obj.ToString();
                        return string.IsNullOrWhiteSpace(s) ? null : s;
                    }
                }
                return null;
            }

            var yCls = GetD("YCls");
            var lstCls = GetD("LstCls");
            var tOpen = GetD("TOpen");
            var tCls = GetD("TCls");

            // if absolutely empty, skip
            if (yCls is null && lstCls is null && tOpen is null && tCls is null)
                continue;

            // ✅ Map only fields that реально є у твоєму SifterDayRowDto
            var row = new SifterDayRowDto
            {
                // CS0029 fix: DateNy is DateOnly in your DTO
                DateNy = dateNy,
                Ticker = it.Ticker,

                YCls = yCls,
                LstCls = lstCls,

                TOpen = tOpen,
                TCls = tCls,

                Gap = GetD("Gap"),
                GapPct = GetD("Gap%"),
                ClsToClsPct = GetD("ClsToCls%"),

                // these are long? in your DTO (per errors)
                Adv20 = GetL("ADV20"),
                Adv90 = GetL("ADV90"),
                PreMktVolNF = GetL("PreMhVolNF"),

                MarketCapM = GetD("MarketCapM"),
                Exchange = GetS("Exchange"),
                SectorL3 = GetS("SectorL3"),

                Beta = GetD("Beta"),
                Sigma = GetD("Sigma"),
            };

            rows.Add(row);
        }

        await _store.WriteAsync(dateNy, rows, ct);

        _log.LogInformation("[SifterIngest] day={Day} requested={Req} items={Items} written={Written}",
            dateNy, list.Length, items.Count, rows.Count);

        return new
        {
            ok = true,
            dateNy = dateNy.ToString("yyyy-MM-dd"),
            requested = list.Length,
            items = items.Count,
            written = rows.Count
        };
    }
}
