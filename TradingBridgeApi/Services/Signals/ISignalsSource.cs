namespace TradingBridgeApi.Signals;

public interface ISignalsSource
{
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);
}
