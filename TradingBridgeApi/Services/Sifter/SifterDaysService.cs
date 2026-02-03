using TradingBridgeApi.Dtos.Sifter;

namespace TradingBridgeApi.Services.Sifter;

public sealed class SifterDaysService
{
    private readonly SifterDayOsbStore _store;

    public SifterDaysService(SifterDayOsbStore store)
    {
        _store = store;
    }

    public async Task<List<SifterDayRowDto>> GetDaysAsync(SifterDaysRequestDto req, CancellationToken ct)
    {
        var result = new List<SifterDayRowDto>();

        for (var d = req.FromDateNy; d <= req.ToDateNy; d = d.AddDays(1))
        {
            var rows = await _store.ReadAsync(d, ct);
            if (rows.Count == 0) continue;

            // server-side filtering
            IEnumerable<SifterDayRowDto> q = rows;

            if (req.Tickers is { Count: > 0 })
            {
                var set = new HashSet<string>(req.Tickers.Select(x => x.Trim().ToUpperInvariant()));
                q = q.Where(r => set.Contains(r.Ticker.ToUpperInvariant()));
            }

            if (req.SectorsL3 is { Count: > 0 })
            {
                var set = new HashSet<string>(req.SectorsL3);
                q = q.Where(r => r.SectorL3 != null && set.Contains(r.SectorL3));
            }

            if (req.MinMarketCapM != null) q = q.Where(r => r.MarketCapM != null && r.MarketCapM >= req.MinMarketCapM);
            if (req.MaxMarketCapM != null) q = q.Where(r => r.MarketCapM != null && r.MarketCapM <= req.MaxMarketCapM);

            if (req.MinGapPct != null) q = q.Where(r => r.GapPct != null && r.GapPct >= req.MinGapPct);
            if (req.MaxGapPct != null) q = q.Where(r => r.GapPct != null && r.GapPct <= req.MaxGapPct);

            if (req.MinClsToClsPct != null) q = q.Where(r => r.ClsToClsPct != null && r.ClsToClsPct >= req.MinClsToClsPct);
            if (req.MaxClsToClsPct != null) q = q.Where(r => r.ClsToClsPct != null && r.ClsToClsPct <= req.MaxClsToClsPct);

            result.AddRange(q);
        }

        return result;
    }
}
