using Microsoft.Extensions.Options;
using Parquet.Serialization;
using TradingBridgeApi.Dtos.Sifter;
using TradingBridgeApi.Options;

namespace TradingBridgeApi.Services.Sifter;

public sealed class SifterDayOsbStore
{
    private readonly SifterOptions _opt;
    private readonly string _dir;

    public SifterDayOsbStore(IOptions<SifterOptions> opt)
    {
        _opt = opt.Value;
        _dir = _opt.GetDayOsbDir();
        Directory.CreateDirectory(_dir);
    }

    public string GetPath(DateOnly dateNy)
        => Path.Combine(_dir, $"{dateNy:yyyy-MM-dd}.parquet");

    public async Task<List<SifterDayRowDto>> ReadAsync(DateOnly dateNy, CancellationToken ct)
    {
        var path = GetPath(dateNy);
        if (!File.Exists(path)) return new List<SifterDayRowDto>();

        await using var fs = File.OpenRead(path);

        // ParquetSerializer returns IList<T> — convert to List<T>
        var rows = await ParquetSerializer.DeserializeAsync<SifterDayRowDto>(fs, cancellationToken: ct);
        return rows is null ? new List<SifterDayRowDto>() : rows.ToList();
    }

    public async Task WriteAsync(DateOnly dateNy, IReadOnlyCollection<SifterDayRowDto> rows, CancellationToken ct)
    {
        var path = GetPath(dateNy);
        await using var fs = File.Create(path);

        await ParquetSerializer.SerializeAsync(rows, fs, cancellationToken: ct);
    }
}
