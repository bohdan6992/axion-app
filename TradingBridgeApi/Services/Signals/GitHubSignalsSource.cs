using System.Net;
using System.Text;
using System.IO.Compression;
using Microsoft.Extensions.Options;

namespace TradingBridgeApi.Signals;

public sealed class GitHubSignalsSource : ISignalsSource
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GitHubSignalsSource> _log;
    private readonly GitHubSignalsOptions _opt;

    public GitHubSignalsSource(
        IHttpClientFactory http,
        IOptions<GitHubSignalsOptions> opt,
        ILogger<GitHubSignalsSource> log)
    {
        _http = http;
        _log = log;
        _opt = opt.Value;
    }

    // =========================================================
    // Strategy-based open (NO manifest)
    // =========================================================
    public async Task<Stream> OpenReadStrategyAsync(string strategy, string fileName, CancellationToken ct)
    {
        var rel = ResolveStrategyPath(strategy?.Trim() ?? "", fileName?.Trim() ?? "");
        return await OpenReadAsync(rel, ct);
    }

    private static string ResolveStrategyPath(string strategy, string fileName)
    {
        if (string.IsNullOrWhiteSpace(strategy))
            throw new ArgumentException("strategy is required", nameof(strategy));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName is required", nameof(fileName));

        // new repo format: root/{strategy}/{files}
        strategy = strategy.Trim().Trim('/').Trim('\\');
        fileName = fileName.Trim().TrimStart('/').TrimStart('\\');

        return $"{strategy}/{fileName}";
    }

    // =========================================================
    // Raw open (with .gz fallback + gunzip)
    // =========================================================
    public async Task<Stream> OpenReadAsync(string relPath, CancellationToken ct)
    {
        relPath = (relPath ?? "").Trim().TrimStart('/');

        var owner = _opt.Owner?.Trim();
        var repo = _opt.Repo?.Trim();
        var branch = string.IsNullOrWhiteSpace(_opt.Branch) ? "main" : _opt.Branch.Trim();

        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("GitHubSignalsOptions.Owner missing");
        if (string.IsNullOrWhiteSpace(repo)) throw new InvalidOperationException("GitHubSignalsOptions.Repo missing");

        var basePath = (_opt.BasePath ?? "").Trim().Trim('/');
        var fullPath = string.IsNullOrWhiteSpace(basePath) ? relPath : $"{basePath}/{relPath}";

        // ---- try cache first ----
        var cachePath = GetCachePath(owner, repo, branch, fullPath);
        if (TryOpenFreshCache(cachePath, out var cached))
            return cached;

        // ---- download (with .gz fallback) ----
        Stream stream;
        try
        {
            stream = await DownloadAndPrepareAsync(owner, repo, branch, fullPath, ct);
        }
        catch (FileNotFoundException) when (!fullPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            // fallback → .gz
            stream = await DownloadAndPrepareAsync(owner, repo, branch, fullPath + ".gz", ct);
        }

        // ---- cache decision ----
        if (!ShouldCache(fullPath, null))
            return stream;

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tmp = cachePath + ".tmp";

        try
        {
            await using (stream)
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, true))
            {
                await stream.CopyToAsync(fs, ct);
            }

            if (File.Exists(cachePath))
                File.Delete(cachePath);

            File.Move(tmp, cachePath);
            return new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // =========================================================
    // Download + LFS + Gunzip
    // =========================================================
    private async Task<Stream> DownloadAndPrepareAsync(
        string owner,
        string repo,
        string branch,
        string fullPath,
        CancellationToken ct)
    {
        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{fullPath}";
        _log.LogInformation("GitHub raw GET {url}", rawUrl);

        var client = _http.CreateClient("github-raw");

        using var req = new HttpRequestMessage(HttpMethod.Get, rawUrl);
        req.Headers.UserAgent.ParseAdd("Axion/1.0");

        if (!string.IsNullOrWhiteSpace(_opt.Token))
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opt.Token);

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new FileNotFoundException($"GitHub raw not found: {rawUrl}", fullPath);
        }

        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadAsStreamAsync(ct);
        var owned = new HttpResponseOwnedStream(resp, raw);

        // ---- detect LFS ----
        var prefix = await ReadPrefixAsync(owned, 2048, ct);
        var txt = Encoding.UTF8.GetString(prefix);

        Stream content;
        if (txt.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
        {
            owned.Dispose();
            var (oid, size) = ParseLfsPointer(txt);
            content = await DownloadLfsObjectAsync(owner, repo, oid, size, ct);
        }
        else
        {
            content = new PrefixStream(prefix, owned);
        }

        // ---- gunzip if needed ----
        if (fullPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return new GZipStream(content, CompressionMode.Decompress);

        return content;
    }

    /* ===================== Cache helpers ===================== */

    private bool TryOpenFreshCache(string path, out Stream stream)
    {
        stream = Stream.Null;
        try
        {
            if (!File.Exists(path)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >
                TimeSpan.FromDays(Math.Max(1, _opt.CacheTtlDays)))
                return false;

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch { return false; }
    }

    private string GetCachePath(string owner, string repo, string branch, string fullPath)
    {
        var safe = fullPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(AxionPaths.AppDataRoot, "cache", "signals", "github",
            owner, repo, branch, safe);
    }

    private bool ShouldCache(string fullPath, long? size)
        => fullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
           || fullPath.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase);

    /* ===================== LFS helpers (unchanged) ===================== */

    private static async Task<byte[]> ReadPrefixAsync(Stream s, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1024];
        while (ms.Length < max)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, max - (int)ms.Length)), ct);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static (string oid, long size) ParseLfsPointer(string txt)
    {
        string? oid = null;
        long size = 0;

        foreach (var line in txt.Split('\n'))
        {
            if (line.StartsWith("oid sha256:")) oid = line[12..].Trim();
            if (line.StartsWith("size ")) long.TryParse(line[5..], out size);
        }

        if (oid == null || size <= 0)
            throw new InvalidOperationException("Invalid LFS pointer");

        return (oid, size);
    }

    private async Task<Stream> DownloadLfsObjectAsync(
        string owner, string repo, string oid, long size, CancellationToken ct)
    {
        // unchanged from your version
        throw new NotImplementedException("LFS code unchanged – keep your existing implementation here");
    }

    /* ===================== Stream wrappers ===================== */

    private sealed class HttpResponseOwnedStream : Stream
    {
        private readonly HttpResponseMessage _resp;
        private readonly Stream _inner;
        public HttpResponseOwnedStream(HttpResponseMessage resp, Stream inner)
        {
            _resp = resp; _inner = inner;
        }
        public override bool CanRead => _inner.CanRead;
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
            => _inner.ReadAsync(b, ct);
        protected override void Dispose(bool d)
        {
            if (d) { _inner.Dispose(); _resp.Dispose(); }
            base.Dispose(d);
        }
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _p;
        private int _pos;
        private readonly Stream _s;
        public PrefixStream(byte[] p, Stream s) { _p = p; _s = s; }
        public override int Read(byte[] b, int o, int c)
        {
            if (_pos < _p.Length)
            {
                var n = Math.Min(c, _p.Length - _pos);
                Buffer.BlockCopy(_p, _pos, b, o, n);
                _pos += n;
                return n;
            }
            return _s.Read(b, o, c);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
        {
            if (_pos < _p.Length)
            {
                var n = Math.Min(b.Length, _p.Length - _pos);
                _p.AsSpan(_pos, n).CopyTo(b.Span);
                _pos += n;
                return ValueTask.FromResult(n);
            }
            return _s.ReadAsync(b, ct);
        }
        protected override void Dispose(bool d) { if (d) _s.Dispose(); base.Dispose(d); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
