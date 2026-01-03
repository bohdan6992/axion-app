namespace TradingBridgeApi.Signals;

public sealed class GitHubSignalsOptions
{
    public string Owner { get; set; } = "bohdan6992";
    public string Repo { get; set; } = "axion-signals";
    public string Branch { get; set; } = "main";

    // optional prefix inside repo, usually empty
    public string? BasePath { get; set; }

    // optional for private repo later
    public string? Token { get; set; }

    // disk cache
    public int CacheTtlDays { get; set; } = 5;

    // cache policy
    public bool CacheAllJsonl { get; set; } = true;

    // if HEAD gives Content-Length and it is >= this threshold → cache
    public long CacheIfSizeAtLeastBytes { get; set; } = 20L * 1024L * 1024L; // 20MB
}
