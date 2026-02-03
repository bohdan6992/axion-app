namespace TradingBridgeApi.Options;

public sealed class SifterOptions
{
    public string RootDir { get; init; } = "sifter";
    public string DayOsbSubdir { get; init; } = "osb_day";

    public string GetDayOsbDir() => Path.Combine(RootDir, DayOsbSubdir);
}
