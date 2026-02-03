using TradingBridgeApi.Dtos.Sifter;
using TradingBridgeApi.Services.Tape; // ваш TapeQueryService / читання parquet

namespace TradingBridgeApi.Services.Sifter;

public sealed class SifterWindowService
{
    private readonly TapeQueryService _tape; // існуючий reader
    private readonly SifterDaysService _days; // щоб обмежити тикери/фільтри через day-rows (опційно)

    public SifterWindowService(TapeQueryService tape, SifterDaysService days)
    {
        _tape = tape;
        _days = days;
    }

    public async Task<SifterWindowResponseDto> RunAsync(SifterWindowRequestDto req, CancellationToken ct)
    {
        // 1) визначаємо eligible tickers (опційно через day rows + sector/mcap)
        // 2) для кожного дня читаємо хвилини [MinuteFrom..MinuteTo] тільки для eligible тикерів
        // 3) беремо тільки колонки metrics
        // 4) рахуємо summary/byDay/byTicker/hist

        // Тут залишаю як “скелет”, бо конкретний доступ до TapeRowV1/reader у вас уже є.
        return new SifterWindowResponseDto();
    }
}
