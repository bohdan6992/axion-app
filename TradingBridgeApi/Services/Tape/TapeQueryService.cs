using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using TradingBridgeApi.Services.Tape.Models;

namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeQueryService
    {
        private readonly TapeFilePaths _paths;

        private const long MinParquetSizeBytes = 8 * 1024; // 8 KB

        public TapeQueryService(TapeFilePaths paths)
        {
            _paths = paths;
        }

        public IReadOnlyList<string> GetAvailableDays()
        {
            if (!Directory.Exists(_paths.Root))
                return Array.Empty<string>();

            return Directory.EnumerateDirectories(_paths.Root, "dateNy=*")
                .Select(Path.GetFileName)
                .Select(name => name?.Split('=', 2))
                .Where(p => p is { Length: 2 } && !string.IsNullOrWhiteSpace(p![1]))
                .Select(p => p![1])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task<IReadOnlyList<string>> GetAvailableDaysAsync(CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<IReadOnlyList<string>>(ct);

            return Task.FromResult(GetAvailableDays());
        }

        public IReadOnlyList<string> GetAvailableNonEmptyDays(CancellationToken ct = default)
        {
            if (!Directory.Exists(_paths.Root))
                return Array.Empty<string>();

            var res = new List<string>();

            foreach (var dir in Directory.EnumerateDirectories(_paths.Root, "dateNy=*"))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(dir) ?? "";
                var parts = name.Split('=', 2);
                if (parts.Length != 2) continue;

                var dateNy = (parts[1] ?? "").Trim();
                if (dateNy.Length == 0) continue;

                if (IsNonEmptyDay(dir, ct))
                    res.Add(dateNy);
            }

            return res
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task<IReadOnlyList<string>> GetAvailableNonEmptyDaysAsync(CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<IReadOnlyList<string>>(ct);

            return Task.FromResult(GetAvailableNonEmptyDays(ct));
        }

        private static bool IsNonEmptyDay(string dateDir, CancellationToken ct)
        {
            try
            {
                // Stage 1: quick size filter
                var candidates = Directory.EnumerateFiles(dateDir, "*.parquet", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .Where(fi => fi.Exists && fi.Length >= MinParquetSizeBytes)
                    .Select(fi => fi.FullName)
                    .ToList();

                if (candidates.Count == 0)
                    return false;

                // Stage 2: peek "Ticker"
                foreach (var p in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (PeekParquetHasAnyTicker(p, ct))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool PeekParquetHasAnyTicker(string parquetPath, CancellationToken ct)
        {
            try
            {
                using var fs = File.OpenRead(parquetPath);
                using var reader = ParquetReader.CreateAsync(fs, cancellationToken: ct).GetAwaiter().GetResult();

                if (reader.RowGroupCount <= 0)
                    return false;

                var tickerField = reader.Schema.Fields
                    .OfType<DataField>()
                    .FirstOrDefault(f => string.Equals(f.Name, "Ticker", StringComparison.OrdinalIgnoreCase));

                if (tickerField == null)
                    return false;

                using var rg = reader.OpenRowGroupReader(0);
                var col = rg.ReadColumnAsync(tickerField, ct).GetAwaiter().GetResult();
                if (col?.Data == null || col.Data.Length == 0)
                    return false;

                var n = Math.Min(col.Data.Length, 64);
                for (int i = 0; i < n; i++)
                {
                    var v = col.Data.GetValue(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool HasMinute(string dateNy, int minuteIdx)
            => File.Exists(_paths.MinuteFile(dateNy, minuteIdx));

        public async Task<IReadOnlyList<TapeRowV1>> QueryAsync(TapeQueryRequest req, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(req.DateNy))
                return Array.Empty<TapeRowV1>();

            var dateNy = req.DateNy.Trim();

            int from = req.MinuteFrom ?? 0;
            int to = req.MinuteTo ?? 1199;

            if (from < 0) from = 0;
            if (to > 1199) to = 1199;
            if (to < from) return Array.Empty<TapeRowV1>();

            HashSet<string>? tickers = null;
            if (req.Tickers is { Length: > 0 })
            {
                tickers = new HashSet<string>(
                    req.Tickers.Where(x => !string.IsNullOrWhiteSpace(x))
                               .Select(x => x.Trim().ToUpperInvariant()),
                    StringComparer.OrdinalIgnoreCase);
            }

            var outRows = new List<TapeRowV1>(capacity: 50_000);

            for (int m = from; m <= to; m++)
            {
                ct.ThrowIfCancellationRequested();

                var file = _paths.MinuteFile(dateNy, m);
                if (!File.Exists(file))
                    continue;

                var rows = await ReadMinuteFileAsync(file, ct);

                foreach (var r in rows)
                {
                    if (tickers != null && !tickers.Contains(r.Ticker))
                        continue;

                    // Optional generic filters
                    if (req.MinZapPct.HasValue)
                    {
                        var mz = req.MinZapPct.Value;
                        var ok = (r.ZapPctS.HasValue && r.ZapPctS.Value >= mz)
                              || (r.ZapPctL.HasValue && r.ZapPctL.Value <= -mz);
                        if (!ok) continue;
                    }

                    if (req.MinSigmaZap.HasValue)
                    {
                        var ms = req.MinSigmaZap.Value;
                        var ok = (r.SigmaZapS.HasValue && r.SigmaZapS.Value >= ms)
                              || (r.SigmaZapL.HasValue && r.SigmaZapL.Value <= -ms);
                        if (!ok) continue;
                    }

                    outRows.Add(r);

                    if (req.Limit > 0 && outRows.Count >= req.Limit)
                        return outRows;
                }
            }

            return outRows;
        }

        private static async Task<List<TapeRowV1>> ReadMinuteFileAsync(string parquetPath, CancellationToken ct)
        {
            await using var fs = File.OpenRead(parquetPath);
            using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

            var result = new List<TapeRowV1>(capacity: 8192);

            for (int g = 0; g < reader.RowGroupCount; g++)
            {
                ct.ThrowIfCancellationRequested();

                using var rg = reader.OpenRowGroupReader(g);
                var schema = reader.Schema;

                var cols = new Dictionary<string, DataColumn>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in schema.Fields.OfType<DataField>())
                {
                    var col = await rg.ReadColumnAsync(f, ct);
                    cols[f.Name] = col;
                }

                int rowCount = cols.Count == 0 ? 0 : cols.First().Value.Data.Length;
                for (int i = 0; i < rowCount; i++)
                    result.Add(MapRow(cols, i));
            }

            return result;
        }

        private static TapeRowV1 MapRow(Dictionary<string, DataColumn> cols, int i)
        {
            string? GetStr(string name)
                => cols.TryGetValue(name, out var c) ? c.Data.GetValue(i)?.ToString() : null;

            int GetInt(string name, int def = 0)
            {
                if (!cols.TryGetValue(name, out var c)) return def;
                var v = c.Data.GetValue(i);
                if (v == null) return def;
                if (v is int ii) return ii;
                if (int.TryParse(v.ToString(), out var x)) return x;
                return def;
            }

            int? GetIntN(string name)
            {
                if (!cols.TryGetValue(name, out var c)) return null;
                var v = c.Data.GetValue(i);
                if (v == null) return null;
                if (v is int ii) return ii;
                if (int.TryParse(v.ToString(), out var x)) return x;
                return null;
            }

            double? GetD(string name)
            {
                if (!cols.TryGetValue(name, out var c)) return null;
                var v = c.Data.GetValue(i);
                if (v == null) return null;
                if (v is double d) return d;
                if (v is float f) return f;
                if (v is decimal dec) return (double)dec;
                if (v is long l) return l;
                if (double.TryParse(v.ToString(), out var x)) return x;
                return null;
            }

            bool? GetB(string name)
            {
                if (!cols.TryGetValue(name, out var c)) return null;
                var v = c.Data.GetValue(i);
                if (v == null) return null;
                if (v is bool b) return b;

                var s = v.ToString()?.Trim();
                if (string.Equals(s, "YES", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "NO", StringComparison.OrdinalIgnoreCase)) return false;
                if (bool.TryParse(s, out var bb)) return bb;
                return null;
            }

            // Prefer LstClose, but fall back to LstCls/YCls if writer doesn't output LstClose.
            var lstClose = GetD(nameof(TapeRowV1.LstClose))
                        ?? GetD(nameof(TapeRowV1.LstCls))
                        ?? GetD(nameof(TapeRowV1.YCls));

            return new TapeRowV1
            {
                // Keys / time
                DateNy = GetStr(nameof(TapeRowV1.DateNy)) ?? "",
                MinuteIdx = GetInt(nameof(TapeRowV1.MinuteIdx)),
                MinuteNy = GetStr(nameof(TapeRowV1.MinuteNy)) ?? "",
                Band = GetStr(nameof(TapeRowV1.Band)) ?? "",
                Ticker = GetStr(nameof(TapeRowV1.Ticker)) ?? "",

                // Quotes (raw)
                Bid = GetD(nameof(TapeRowV1.Bid)),
                Ask = GetD(nameof(TapeRowV1.Ask)),
                Mid = GetD(nameof(TapeRowV1.Mid)),
                Spread = GetD(nameof(TapeRowV1.Spread)),
                SpreadBps = GetD(nameof(TapeRowV1.SpreadBps)),

                // Quotes (pct-space)
                BidPct = GetD(nameof(TapeRowV1.BidPct)),
                AskPct = GetD(nameof(TapeRowV1.AskPct)),

                // SCOPE fill / move (pct-space)
                LstPrcLstClsPct = GetD(nameof(TapeRowV1.LstPrcLstClsPct)),
                LstPrcTOpenPct = GetD(nameof(TapeRowV1.LstPrcTOpenPct)),

                // Price context / TRAP daily context
                TOpen = GetD(nameof(TapeRowV1.TOpen)),
                TCls = GetD(nameof(TapeRowV1.TCls)),
                ATR14 = GetD(nameof(TapeRowV1.ATR14)),
                Hi = GetD(nameof(TapeRowV1.Hi)),

                // Price context (existing)
                LstClose = lstClose,
                VWAP = GetD(nameof(TapeRowV1.VWAP)),
                Lo = GetD(nameof(TapeRowV1.Lo)),

                // TRAP OSB day fields
                YCls = GetD(nameof(TapeRowV1.YCls)),
                LstCls = GetD(nameof(TapeRowV1.LstCls)),

                Gap = GetD(nameof(TapeRowV1.Gap)),
                GapPct = GetD(nameof(TapeRowV1.GapPct)),
                ClsToClsPct = GetD(nameof(TapeRowV1.ClsToClsPct)),

                // Liquidity / volumes / notional
                Vol = GetD(nameof(TapeRowV1.Vol)),
                PreMktVol = GetD(nameof(TapeRowV1.PreMktVol)),
                PreMktVolNF = GetD(nameof(TapeRowV1.PreMktVolNF)),
                Adv20 = GetD(nameof(TapeRowV1.Adv20)),
                Adv90 = GetD(nameof(TapeRowV1.Adv90)),
                Adv20NF = GetD(nameof(TapeRowV1.Adv20NF)),
                Adv90NF = GetD(nameof(TapeRowV1.Adv90NF)),

                // Flags
                IsPTP = GetB(nameof(TapeRowV1.IsPTP)),
                IsSSR = GetB(nameof(TapeRowV1.IsSSR)),
                OutThSSR = GetB(nameof(TapeRowV1.OutThSSR)),
                IsETF = GetB(nameof(TapeRowV1.IsETF)),
                HasReport = GetB(nameof(TapeRowV1.HasReport)),
                NewsCnt = GetIntN(nameof(TapeRowV1.NewsCnt)),
                HasNews = GetB(nameof(TapeRowV1.HasNews)),
                IsCrap = GetB(nameof(TapeRowV1.IsCrap)),

                // Bench
                BenchTicker = GetStr(nameof(TapeRowV1.BenchTicker)),
                BenchBid = GetD(nameof(TapeRowV1.BenchBid)),
                BenchAsk = GetD(nameof(TapeRowV1.BenchAsk)),
                BenchLstCls = GetD(nameof(TapeRowV1.BenchLstCls)),
                BenchBidPct = GetD(nameof(TapeRowV1.BenchBidPct)),
                BenchAskPct = GetD(nameof(TapeRowV1.BenchAskPct)),

                // Arbitrage-ready
                ZapPctS = GetD(nameof(TapeRowV1.ZapPctS)),
                ZapPctL = GetD(nameof(TapeRowV1.ZapPctL)),
                SigmaZapS = GetD(nameof(TapeRowV1.SigmaZapS)),
                SigmaZapL = GetD(nameof(TapeRowV1.SigmaZapL)),
                TruePriceS = GetD(nameof(TapeRowV1.TruePriceS)),
                TruePriceL = GetD(nameof(TapeRowV1.TruePriceL)),
                ShortCandidate = GetB(nameof(TapeRowV1.ShortCandidate)),
                LongCandidate = GetB(nameof(TapeRowV1.LongCandidate)),

                // Legacy alias
                Sig = GetD(nameof(TapeRowV1.Sig)),

                // Meta / static
                TierBp = GetD(nameof(TapeRowV1.TierBp)),
                MarketCapM = GetD(nameof(TapeRowV1.MarketCapM)),
                Exchange = GetStr(nameof(TapeRowV1.Exchange)),
                Country = GetStr(nameof(TapeRowV1.Country)),
                SectorL3 = GetStr(nameof(TapeRowV1.SectorL3)),
                RoundLot = GetIntN(nameof(TapeRowV1.RoundLot)),

                // STATIC enrich
                Beta = GetD(nameof(TapeRowV1.Beta)),
                Sigma = GetD(nameof(TapeRowV1.Sigma))
            };
        }
    }
}
