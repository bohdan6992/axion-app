using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace TradingBridgeApi.Services.Tape
{
    public sealed class TapeRetentionService
    {
        private readonly TapeFilePaths _paths;
        private readonly ILogger<TapeRetentionService> _log;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // Heuristic thresholds (fast filter before parquet peek)
        private const long MinParquetSizeBytes = 8 * 1024; // 8 KB

        // Скільки останніх НЕпорожніх днів тримаємо
        private const int NonEmptyRetentionDays = 30;

        public TapeRetentionService(TapeFilePaths paths, ILogger<TapeRetentionService> log)
        {
            _paths = paths;
            _log = log;
        }

        /// <summary>
        /// Backward-compatible alias (older code may call ApplyAsync).
        /// </summary>
        public Task ApplyAsync(CancellationToken ct = default)
            => EnforceRetentionAsync(ct);

        /// <summary>
        /// Keep last 30 NonEmpty NY days under _paths.Root.
        /// "NonEmptyDay" = day has at least one parquet minute file
        /// with real Bid/Ask (тобто хоч один рядок, де Bid або Ask не null).
        /// Empty/null days do NOT count toward retention and may be deleted as junk.
        /// </summary>
        public async Task EnforceRetentionAsync(CancellationToken ct = default)
        {
            if (!Directory.Exists(_paths.Root))
                return;

            // Scan all dateNy=* dirs
            var dayDirs = new List<(string Dir, DateOnly Date, string DateStr)>();
            foreach (var dir in Directory.EnumerateDirectories(_paths.Root, "dateNy=*"))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(dir) ?? "";
                if (!TryParseDateNyDir(name, out var date))
                    continue;

                var dateStr = date.ToString("yyyy-MM-dd", Inv);
                dayDirs.Add((dir, date, dateStr));
            }

            // newest first
            dayDirs.Sort((a, b) => b.Date.CompareTo(a.Date));

            var availableDays = dayDirs.Select(x => x.DateStr).ToList();

            // Determine NonEmptyDay
            var nonEmpty = new List<(string Dir, DateOnly Date, string DateStr)>();
            var empty = new List<(string Dir, DateOnly Date, string DateStr)>();

            foreach (var d in dayDirs)
            {
                ct.ThrowIfCancellationRequested();

                var isNonEmpty = await IsNonEmptyDayAsync(d.Dir, ct);
                if (isNonEmpty) nonEmpty.Add(d);
                else empty.Add(d);
            }

            // Keep last N non-empty days
            var keep = nonEmpty
                .Take(NonEmptyRetentionDays)
                .ToDictionary(x => x.DateStr, x => x.Dir, StringComparer.OrdinalIgnoreCase);

            // Delete empty days (junk) + non-empty over retention
            var deletedEmpty = new List<string>();
            var deletedOver = new List<string>();

            foreach (var d in empty)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(d.Dir, recursive: true);
                    deletedEmpty.Add(d.DateStr);
                    _log.LogInformation("[TapeRetention] deleted EMPTY {dateNy} ({dir})", d.DateStr, d.Dir);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TapeRetention] delete failed (EMPTY) {dateNy} ({dir})", d.DateStr, d.Dir);
                }
            }

            foreach (var d in nonEmpty.Skip(NonEmptyRetentionDays))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Directory.Delete(d.Dir, recursive: true);
                    deletedOver.Add(d.DateStr);
                    _log.LogInformation("[TapeRetention] deleted OVER {dateNy} ({dir})", d.DateStr, d.Dir);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TapeRetention] delete failed (OVER) {dateNy} ({dir})", d.DateStr, d.Dir);
                }
            }

            // Summary log (single block)
            _log.LogInformation(
                "[TapeRetention] availableDays={available} nonEmptyDays={nonEmpty} keepLast{n}NonEmpty={keep} deletedEmpty={delEmpty} deletedOver={delOver}",
                string.Join(",", availableDays),
                string.Join(",", nonEmpty.Select(x => x.DateStr)),
                NonEmptyRetentionDays,
                string.Join(",", keep.Keys),
                string.Join(",", deletedEmpty),
                string.Join(",", deletedOver)
            );
        }

        private static bool TryParseDateNyDir(string dirName, out DateOnly date)
        {
            date = default;

            // expected: dateNy=YYYY-MM-DD
            var parts = dirName.Split('=', 2);
            if (parts.Length != 2) return false;
            if (!string.Equals(parts[0], "dateNy", StringComparison.OrdinalIgnoreCase)) return false;

            var s = (parts[1] ?? "").Trim();
            return DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out date);
        }

        private static IEnumerable<string> EnumerateParquetFiles(string dateDir)
        {
            // minute files are usually nested by minute; safest: scan all *.parquet under day dir
            return Directory.EnumerateFiles(dateDir, "*.parquet", SearchOption.AllDirectories);
        }

        /// <summary>
        /// NonEmptyDay = є хоч один parquet-файл, де в перших N рядках є Bid або Ask != null.
        /// </summary>
        private static async Task<bool> IsNonEmptyDayAsync(string dateDir, CancellationToken ct)
        {
            try
            {
                // Stage 1: quick size filter
                var candidates = new List<string>();
                foreach (var f in EnumerateParquetFiles(dateDir))
                {
                    ct.ThrowIfCancellationRequested();
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length >= MinParquetSizeBytes)
                        candidates.Add(f);
                }

                if (candidates.Count == 0)
                    return false;

                // Stage 2: peek parquet for any Bid/Ask
                foreach (var p in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await PeekParquetHasAnyBidAskAsync(p, ct))
                        return true;
                }

                return false;
            }
            catch
            {
                // Якщо перевірка фейлиться — вважаємо день порожнім, щоб не тримати сміття.
                return false;
            }
        }

        private static async Task<bool> PeekParquetHasAnyBidAskAsync(string parquetPath, CancellationToken ct)
        {
            try
            {
                await using var fs = File.OpenRead(parquetPath);
                using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct);

                if (reader.RowGroupCount <= 0)
                    return false;

                using var rg = reader.OpenRowGroupReader(0);
                var schema = reader.Schema;

                var bidField = schema.Fields
                    .OfType<DataField>()
                    .FirstOrDefault(f => string.Equals(f.Name, "Bid", StringComparison.OrdinalIgnoreCase));

                var askField = schema.Fields
                    .OfType<DataField>()
                    .FirstOrDefault(f => string.Equals(f.Name, "Ask", StringComparison.OrdinalIgnoreCase));

                if (bidField == null && askField == null)
                    return false;

                DataColumn? bidCol = null;
                DataColumn? askCol = null;

                if (bidField != null)
                    bidCol = await rg.ReadColumnAsync(bidField, ct);

                if (askField != null)
                    askCol = await rg.ReadColumnAsync(askField, ct);

                var rowCount =
                    bidCol?.Data.Length
                    ?? askCol?.Data.Length
                    ?? 0;

                if (rowCount == 0)
                    return false;

                var maxRows = Math.Min(rowCount, 500);

                for (int i = 0; i < maxRows; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var bid = bidCol?.Data.GetValue(i);
                    var ask = askCol?.Data.GetValue(i);

                    if (bid != null || ask != null)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
