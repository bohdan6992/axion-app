// TradingAppClient.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.ExternalAPI; // типи з TradingAppAPI.dll

namespace TradingBridgeApi;

public class TradingAppClient
{
    private readonly DataClient _client;

    // ✅ Якщо DLL/DataClient НЕ thread-safe (дуже ймовірно) — серіалізуємо доступ.
    // Один процес -> одна черга доступу до DLL.
    private static readonly SemaphoreSlim _dllGate = new(1, 1);

    // Дефолтний набір тікерів, якщо в /api/quotes не передали ?tickers=
    public static readonly string[] DefaultTickers = new[]
    {
        "AAPL", "MSFT", "META", "AMZN", "GOOGL",
        "SPY", "QQQ", "IWM", "XLF", "XLE"
    };

    // Дефолтний набір полів, якщо явно не задані
    public static readonly string[] DefaultFields = new[]
    {
        "BidLstClsΔ%", "AskLstClsΔ%", "Bid", "Ask", "Exchange"
    };

    public TradingAppClient()
    {
        _client = new DataClient();
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Парсить число з рядка максимально безпечно:
    /// - підтримує юнікодні мінуси
    /// - підтримує %, K/M/B (1.2K, 3.4M, 1.1B)
    /// - підтримує коми як десятковий роздільник
    /// - не ламає випадки з текстом (повертає null, якщо це не "схоже" на число)
    /// </summary>
    private static double? ToNum(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;

        var s = v.Trim();

        // нормалізуємо юнікодні мінуси/дефіси
        s = s.Replace("−", "-")
             .Replace("–", "-")
             .Replace("—", "-");

        // прибираємо пробіли
        s = s.Replace(" ", "");

        // Витягуємо суфікс множника (K/M/B), якщо є в кінці
        double mult = 1.0;
        if (s.Length > 0)
        {
            char last = char.ToUpperInvariant(s[^1]);
            if (last == 'K' || last == 'M' || last == 'B')
            {
                mult = last switch
                {
                    'K' => 1_000.0,
                    'M' => 1_000_000.0,
                    'B' => 1_000_000_000.0,
                    _ => 1.0
                };
                s = s.Substring(0, s.Length - 1);
            }
        }

        // прибираємо % (але пам’ятаємо, що це відсотки — множник не міняємо)
        if (s.EndsWith("%", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - 1);

        // кома -> крапка
        s = s.Replace(",", ".");

        // Якщо взагалі немає цифр — не число
        if (!s.Any(char.IsDigit))
            return null;

        // залишаємо тільки цифри, знаки та крапку
        var clean = new string(s.Where(ch =>
            char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+'
        ).ToArray());

        if (clean.Length == 0)
            return null;

        if (double.TryParse(clean, NumberStyles.Any, Inv, out var d))
            return d * mult;

        return null;
    }

    /// <summary>
    /// Безпечний split для TRAP/TradingAppAPI:
    /// - НЕ видаляє порожні значення (інакше з’їде індексація)
    /// - прибирає хвостові "" якщо рядок закінчується на ';'
    /// - лікує shift, якщо рядок почався з ';' (тобто parts[0]=="" і довжина expected+1)
    /// - вирівнює довжину до expectedLen (trim/pad)
    /// </summary>
    private static string[] SplitTrapLine(string? raw, int expectedLen)
    {
        raw ??= "";

        // ВАЖЛИВО: не RemoveEmptyEntries
        var parts = raw.Split(';', StringSplitOptions.TrimEntries);

        // прибираємо хвіст "" якщо є через trailing ';'
        if (parts.Length > 0 && parts[^1].Length == 0)
        {
            int newLen = parts.Length;
            while (newLen > 0 && parts[newLen - 1].Length == 0)
                newLen--;

            Array.Resize(ref parts, newLen);
        }

        // ✅ shift fix: інколи приходить лідируючий ';' => parts[0]=="" і елементів expected+1
        if (parts.Length == expectedLen + 1 && parts.Length > 0 && parts[0].Length == 0)
        {
            parts = parts.Skip(1).ToArray();
        }

        // Особливий кейс: expectedLen=1, а parts = ["", "VAL"] — теж зсув
        if (expectedLen == 1 && parts.Length >= 2 && parts[0].Length == 0 && parts[1].Length > 0)
        {
            parts = new[] { parts[1] };
        }

        // вирівнюємо до expectedLen
        if (parts.Length != expectedLen)
        {
            var aligned = new string[expectedLen];
            var n = Math.Min(parts.Length, expectedLen);
            Array.Copy(parts, aligned, n);
            for (int i = n; i < expectedLen; i++)
                aligned[i] = "";
            parts = aligned;
        }

        return parts;
    }

    /// <summary>
    /// Евристика: чи "схоже" значення на число.
    /// Це дозволяє не ламати Company/Country/Sector та інші текстові поля,
    /// навіть якщо вони не в whitelist.
    /// </summary>
    private static bool LooksNumeric(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        // Якщо є хоч одна цифра — часто це число/%, або число з K/M/B
        if (!s.Any(char.IsDigit)) return false;

        // Якщо є букви (крім K/M/B на кінці) — швидше за все текст
        // (Exchange типу "NSDQ" -> цифр нема, вже відфільтровано)
        // Company може містити цифри (наприклад "3M") — тоді краще не примушувати в число
        // Тому: дозволяємо тільки K/M/B як суфікс, інакше якщо багато букв — вважаємо текстом.
        var letters = s.Count(char.IsLetter);
        if (letters == 0) return true;

        // якщо рівно 1 літера і це K/M/B в кінці — ок
        if (letters == 1)
        {
            char last = char.ToUpperInvariant(s[^1]);
            if ((last == 'K' || last == 'M' || last == 'B') && s.Count(char.IsLetter) == 1)
                return true;
        }

        // У інших випадках не насилуємо в число
        return false;
    }

    /// <summary>
    /// Базовий синхронний метод:
    /// Повертає словник: Ticker -> Field -> Value (double або raw string)
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> GetQuotes(
        string[] tickers,
        string[] fields)
    {
        // прибираємо дублікати полів (економить DLL calls, зменшує шанс transient fail)
        var uniqFields = fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tickers)
            result[t] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in uniqFields)
        {
            // ВАЖЛИВО: в DLL тип називається TikerFieldModel (без c!)
            var models = new List<TikerFieldModel>(tickers.Length);
            foreach (var t in tickers)
            {
                models.Add(new TikerFieldModel
                {
                    Ticker = t,
                    FieldName = field
                });
            }

            // TradingAppAPI повертає рядок з values, розділених ';'
            string raw = _client.GetDataFromTradingApp(models);

            // ✅ дуже легкий ретрай на явну порожнечу
            if (string.IsNullOrWhiteSpace(raw))
            {
                Thread.Sleep(50);
                raw = _client.GetDataFromTradingApp(models);
            }

            var parts = SplitTrapLine(raw, tickers.Length);

            for (int i = 0; i < tickers.Length; i++)
            {
                var ticker = tickers[i];
                var valueStr = parts[i];

                // ✅ Не намагайся перетворити в число "все підряд".
                // Якщо виглядає як число — парсимо, інакше віддаємо string як є.
                if (LooksNumeric(valueStr))
                {
                    var num = ToNum(valueStr);
                    result[ticker][field] = num ?? (object?)valueStr;
                }
                else
                {
                    result[ticker][field] = valueStr;
                }
            }
        }

        return result;
    }

    public Task<Dictionary<string, Dictionary<string, object?>>> GetQuotesAsync(
        string[] tickers,
        CancellationToken ct = default)
    {
        return GetQuotesAsync(tickers, DefaultFields, ct);
    }

    public async Task<Dictionary<string, Dictionary<string, object?>>> GetQuotesAsync(
        string[] tickers,
        string[] fields,
        CancellationToken ct = default)
    {
        // ✅ ВАЖЛИВО: без Task.Run (не плодимо конкурентні виконання)
        await _dllGate.WaitAsync(ct);
        try
        {
            return GetQuotes(tickers, fields);
        }
        finally
        {
            _dllGate.Release();
        }
    }

    public async Task<object> RunStressTestAsync(
        int count,
        int rounds,
        CancellationToken ct = default)
    {
        if (count <= 0) count = 1;
        if (rounds <= 0) rounds = 1;

        var tickers = Enumerable.Repeat("SPY", count).ToArray();
        var fields = DefaultFields;

        var roundsStats = new List<object>();
        var totalSw = Stopwatch.StartNew();

        for (int i = 1; i <= rounds; i++)
        {
            ct.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            var res = await GetQuotesAsync(tickers, fields, ct);
            sw.Stop();

            roundsStats.Add(new
            {
                round = i,
                ms = sw.Elapsed.TotalMilliseconds,
                items = res.Count
            });
        }

        totalSw.Stop();

        return new
        {
            count,
            rounds,
            totalMs = totalSw.Elapsed.TotalMilliseconds,
            avgMs = totalSw.Elapsed.TotalMilliseconds / rounds,
            roundsStats
        };
    }
}
