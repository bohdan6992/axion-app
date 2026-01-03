using System.Globalization;
using TradingBridgeApi.StrategyCommon.Dtos;

namespace TradingBridgeApi.StrategyCommon.Signals;

public sealed class EligibilityPolicy
{
    private const decimal MinRateFloor = 0.3m;
    private const int MinTotalFloor = 3;

    public IEnumerable<SignalItemDto> Apply(IEnumerable<SignalItemDto> items, SignalsQueryDto q)
    {
        var minRateReq = q.MinRate;
        var minTotalReq = q.MinTotal;

        var minRate = minRateReq < MinRateFloor ? MinRateFloor : minRateReq;
        var minTotal = minTotalReq < MinTotalFloor ? MinTotalFloor : minTotalReq;

        int seen = 0, pass = 0;
        int dropMissing = 0, dropTotal = 0, dropRate = 0;

        const int MaxEx = 8;
        var exMissing = new List<string>(MaxEx);
        var exTotal = new List<string>(MaxEx);
        var exRate = new List<string>(MaxEx);

        foreach (var it in items)
        {
            seen++;

            // total/rating: беремо спочатку з item, якщо там 0 — беремо з it.Best.*
            var hasTotal = TryResolveTotal(it, out var total);
            var hasRating = TryResolveRating(it, out var rating);

            if (!hasTotal || !hasRating)
            {
                dropMissing++;
                if (exMissing.Count < MaxEx) exMissing.Add(it.Ticker ?? "?");
                continue;
            }

            if (total < minTotal)
            {
                dropTotal++;
                if (exTotal.Count < MaxEx) exTotal.Add((it.Ticker ?? "?") + ":" + total);
                continue;
            }

            if (rating < minRate)
            {
                dropRate++;
                if (exRate.Count < MaxEx) exRate.Add((it.Ticker ?? "?") + ":" + rating.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            pass++;
            yield return it;
        }

        Console.WriteLine(
            "[ELIG] seen=" + seen +
            " pass=" + pass +
            " minRateReq=" + minRateReq.ToString(CultureInfo.InvariantCulture) +
            " minTotalReq=" + minTotalReq +
            " -> floors(minRate=" + minRate.ToString(CultureInfo.InvariantCulture) +
            ", minTotal=" + minTotal + ")" +
            " dropMissing=" + dropMissing +
            " dropTotal=" + dropTotal +
            " dropRate=" + dropRate);

        if (exMissing.Count > 0) Console.WriteLine("[ELIG] ex missing Total/Rating: " + string.Join(", ", exMissing));
        if (exTotal.Count > 0) Console.WriteLine("[ELIG] ex total<minTotal: " + string.Join(", ", exTotal));
        if (exRate.Count > 0) Console.WriteLine("[ELIG] ex rating<minRate: " + string.Join(", ", exRate));
    }

    private static bool TryResolveTotal(SignalItemDto it, out int total)
    {
        total = 0;

        // якщо item.Total заповнений
        if (it.Total > 0)
        {
            total = it.Total;
            return true;
        }

        // fallback: Best.Total
        return TryReadIntProperty(it.Best, "Total", out total);
    }

    private static bool TryResolveRating(SignalItemDto it, out decimal rating)
    {
        rating = 0m;

        // якщо item.Rating заповнений
        if (it.Rating > 0m)
        {
            rating = it.Rating;
            return true;
        }

        // fallback: Best.Rating
        return TryReadDecimalProperty(it.Best, "Rating", out rating);
    }

    private static bool TryReadIntProperty(object? obj, string propName, out int value)
    {
        value = 0;
        if (obj is null) return false;

        var p = obj.GetType().GetProperty(propName);
        if (p is null || !p.CanRead) return false;

        var raw = p.GetValue(obj);
        if (raw is null) return false;

        if (raw is int i) { value = i; return true; }
        if (raw is long l) { value = (int)l; return true; }
        if (raw is decimal d) { value = (int)d; return true; }
        if (raw is double db) { value = (int)db; return true; }

        var s = raw.ToString();
        if (!string.IsNullOrWhiteSpace(s) &&
            int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryReadDecimalProperty(object? obj, string propName, out decimal value)
    {
        value = 0m;
        if (obj is null) return false;

        var p = obj.GetType().GetProperty(propName);
        if (p is null || !p.CanRead) return false;

        var raw = p.GetValue(obj);
        if (raw is null) return false;

        if (raw is decimal d) { value = d; return true; }
        if (raw is double db) { value = (decimal)db; return true; }
        if (raw is float f) { value = (decimal)f; return true; }
        if (raw is int i) { value = i; return true; }
        if (raw is long l) { value = l; return true; }

        var s = raw.ToString();
        if (!string.IsNullOrWhiteSpace(s) &&
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
