using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TradingBridgeApi.Signals;

public sealed class GitHubSignalsSource : ISignalsSource
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GitHubSignalsSource> _log;
    private readonly GitHubSignalsOptions _opt;

    // ---- manifest cache ----
    private readonly object _mfLock = new();
    private ManifestDto? _mf;
    private DateTime _mfAtUtc;

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
    // ✅ NEW: strategy-based open (uses _meta/manifest.json)
    // =========================================================
    public async Task<Stream> OpenReadStrategyAsync(string strategy, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(strategy))
            throw new ArgumentException("strategy is required", nameof(strategy));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName is required", nameof(fileName));

        var rel = await ResolveStrategyPathAsync(strategy.Trim(), fileName.Trim(), ct);
        return await OpenReadAsync(rel, ct);
    }

    // =========================================================
    // ✅ Existing: raw relPath open (direct)
    // =========================================================
    public async Task<Stream> OpenReadAsync(string relPath, CancellationToken ct)
    {
        relPath = (relPath ?? "").Trim().TrimStart('/');

        var owner = (_opt.Owner ?? "").Trim();
        var repo = (_opt.Repo ?? "").Trim();
        var branch = string.IsNullOrWhiteSpace(_opt.Branch) ? "main" : _opt.Branch.Trim();

        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("GitHubSignalsOptions.Owner missing");
        if (string.IsNullOrWhiteSpace(repo)) throw new InvalidOperationException("GitHubSignalsOptions.Repo missing");

        var basePath = (_opt.BasePath ?? "").Trim().Trim('/');
        var fullPath = string.IsNullOrWhiteSpace(basePath) ? relPath : $"{basePath}/{relPath}";

        _log.LogInformation("GitHubSignalsSource OpenRead relPath={relPath} basePath={basePath} fullPath={fullPath}",
            relPath, basePath, fullPath);

        // 1) disk cache
        var cachePath = GetCachePath(owner, repo, branch, fullPath);
        if (TryOpenFreshCache(cachePath, out var cached))
            return cached;

        // 2) download raw
        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{fullPath}";
        _log.LogInformation("GitHubSignalsSource GET raw: {rawUrl}", rawUrl);

        var client = _http.CreateClient("github-raw");

        var getReq = new HttpRequestMessage(HttpMethod.Get, rawUrl);
        getReq.Headers.UserAgent.ParseAdd("Axion/1.0");
        if (!string.IsNullOrWhiteSpace(_opt.Token))
            getReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opt.Token);

        // IMPORTANT: don't dispose response here; we must keep it alive while reading stream
        var getResp = await client.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);

        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            getResp.Dispose();
            throw new FileNotFoundException($"GitHub raw file not found: {rawUrl}", relPath);
        }

        if (getResp.StatusCode == HttpStatusCode.Unauthorized || getResp.StatusCode == HttpStatusCode.Forbidden)
        {
            var code = (int)getResp.StatusCode;
            getResp.Dispose();
            throw new InvalidOperationException($"GitHub raw access denied ({code}). If this is LFS/private repo, set Axion:Signals:GitHub:Token.");
        }

        getResp.EnsureSuccessStatusCode();

        var rawStream = await getResp.Content.ReadAsStreamAsync(ct);

        // Wrap so disposing the returned stream will dispose the HttpResponseMessage too
        var responseStream = new HttpResponseOwnedStream(getResp, rawStream);

        // 3) peek prefix (small) to detect LFS pointer
        var prefix = await ReadPrefixAsync(responseStream, 2048, ct);
        var prefixTxt = Encoding.UTF8.GetString(prefix);

        Stream finalStream;
        long? finalSizeHint = getResp.Content.Headers.ContentLength;

        if (prefixTxt.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
        {
            // pointer -> need LFS download
            responseStream.Dispose(); // disposes response too

            var (oid, size) = ParseLfsPointer(prefixTxt);
            finalSizeHint = size;

            finalStream = await DownloadLfsObjectAsync(owner, repo, oid, size, ct);
        }
        else
        {
            // real file -> push prefix back in front of remaining stream
            finalStream = new PrefixStream(prefix, responseStream);
        }

        // 4) cache decision
        if (!ShouldCache(fullPath, finalSizeHint))
            return finalStream;

        // 5) write to cache (streaming), then reopen cached file
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var tmp = cachePath + ".tmp";
        try
        {
            await using (finalStream.ConfigureAwait(false))
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, useAsync: true))
            {
                await finalStream.CopyToAsync(fs, 1024 * 256, ct);
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
    // ✅ NEW: manifest-based resolution
    // =========================================================
    private async Task<string> ResolveStrategyPathAsync(string strategy, string fileName, CancellationToken ct)
    {
        var mf = await GetManifestAsync(ct);

        if (mf.Strategies is null || !mf.Strategies.TryGetValue(strategy, out var s) || s is null)
            throw new FileNotFoundException($"Manifest: strategy '{strategy}' not found", strategy);

        // Validate file exists in manifest list (optional but helpful)
        if (s.Files is not null && s.Files.Count > 0)
        {
            var ok = s.Files.Any(x => string.Equals(x, fileName, StringComparison.OrdinalIgnoreCase));
            if (!ok)
                throw new FileNotFoundException($"Manifest: file '{fileName}' not listed for strategy '{strategy}'", fileName);
        }

        var dir = (s.Dir ?? strategy).Trim().Trim('/'); // fallback: dir==strategy
        var rel = string.IsNullOrWhiteSpace(dir) ? fileName : $"{dir}/{fileName}";
        return rel;
    }

    private async Task<ManifestDto> GetManifestAsync(CancellationToken ct)
    {
        // TTL: 60s (щоб можна було оновлювати manifest без рестарту)
        var ttl = TimeSpan.FromSeconds(60);

        lock (_mfLock)
        {
            if (_mf is not null && (DateTime.UtcNow - _mfAtUtc) < ttl)
                return _mf;
        }

        // read fresh
        await using var s = await OpenReadAsync("_meta/manifest.json", ct);
        using var sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
        var json = await sr.ReadToEndAsync(ct);

        var mf = JsonSerializer.Deserialize<ManifestDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (mf is null)
            throw new InvalidOperationException("Manifest parse failed: _meta/manifest.json");

        lock (_mfLock)
        {
            _mf = mf;
            _mfAtUtc = DateTime.UtcNow;
        }

        return mf;
    }

    private sealed class ManifestDto
    {
        public string? UpdatedAtUtc { get; set; }
        public Dictionary<string, StrategyDto>? Strategies { get; set; }
    }

    private sealed class StrategyDto
    {
        public string? Dir { get; set; } // ✅ new (recommended)
        public List<string>? Files { get; set; }
    }

    /* ===================== Cache helpers ===================== */

    private bool TryOpenFreshCache(string path, out Stream stream)
    {
        stream = Stream.Null;

        try
        {
            if (!File.Exists(path))
                return false;

            var ttl = TimeSpan.FromDays(Math.Max(1, _opt.CacheTtlDays));
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > ttl)
                return false;

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetCachePath(string owner, string repo, string branch, string fullPath)
    {
        // AppData\Axion\cache\signals\github\{owner}\{repo}\{branch}\{fullPath}
        var root = AxionPaths.AppDataRoot;
        var safePath = fullPath.Replace('/', Path.DirectorySeparatorChar);

        return Path.Combine(root, "cache", "signals", "github", owner, repo, branch, safePath);
    }

    private bool ShouldCache(string fullPath, long? sizeHint)
    {
        var isJsonl = fullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

        if (_opt.CacheAllJsonl && isJsonl)
            return true;

        if (sizeHint.HasValue && sizeHint.Value >= _opt.CacheIfSizeAtLeastBytes)
            return true;

        return false;
    }

    /* ===================== Raw prefix & LFS ===================== */

    private static async Task<byte[]> ReadPrefixAsync(Stream s, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1024];

        while (ms.Length < maxBytes)
        {
            var toRead = Math.Min(buf.Length, maxBytes - (int)ms.Length);
            var n = await s.ReadAsync(buf.AsMemory(0, toRead), ct);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }

        return ms.ToArray();
    }

    private static (string oid, long size) ParseLfsPointer(string txt)
    {
        string? oid = null;
        long size = 0;

        foreach (var line in txt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("oid sha256:", StringComparison.OrdinalIgnoreCase))
                oid = line.Substring("oid sha256:".Length).Trim();

            if (line.StartsWith("size ", StringComparison.OrdinalIgnoreCase))
                long.TryParse(line.Substring("size ".Length).Trim(), out size);
        }

        if (string.IsNullOrWhiteSpace(oid) || size <= 0)
            throw new InvalidOperationException("Invalid Git LFS pointer: cannot parse oid/size.");

        return (oid!, size);
    }

    private async Task<Stream> DownloadLfsObjectAsync(
        string owner,
        string repo,
        string oid,
        long size,
        CancellationToken ct)
    {
        var hasToken = !string.IsNullOrWhiteSpace(_opt.Token);

        // GitHub LFS batch API
        var batchUrl = $"https://github.com/{owner}/{repo}.git/info/lfs/objects/batch";

        // LFS typically uses Basic auth; token as password works.
        var basic = hasToken
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{owner}:{_opt.Token}"))
            : null;

        var client = _http.CreateClient("github-raw");

        using var req = new HttpRequestMessage(HttpMethod.Post, batchUrl);
        req.Headers.UserAgent.ParseAdd("Axion/1.0");
        if (basic is not null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);

        var payload = new
        {
            operation = "download",
            transfers = new[] { "basic" },
            objects = new[] { new { oid, size } }
        };

        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct);

        if (!hasToken && (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden))
            throw new InvalidOperationException("Git LFS download requires Axion:Signals:GitHub:Token (PAT).");

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var obj = doc.RootElement.GetProperty("objects")[0];

        if (obj.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg = err.TryGetProperty("message", out var m) ? m.ToString() : "unknown";
            throw new InvalidOperationException($"LFS batch error: {code} {msg}");
        }

        var download = obj.GetProperty("actions").GetProperty("download");
        var href = download.GetProperty("href").GetString();
        if (string.IsNullOrWhiteSpace(href))
            throw new InvalidOperationException("LFS batch response missing download href");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (download.TryGetProperty("header", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in hdrEl.EnumerateObject())
                headers[p.Name] = p.Value.GetString() ?? "";
        }

        var dreq = new HttpRequestMessage(HttpMethod.Get, href);
        dreq.Headers.UserAgent.ParseAdd("Axion/1.0");

        foreach (var kv in headers)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
                dreq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        // fallback auth (if no special header in batch)
        if (basic is not null && !dreq.Headers.Contains("Authorization"))
            dreq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);

        var dresp = await client.SendAsync(dreq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!hasToken && (dresp.StatusCode == HttpStatusCode.Unauthorized || dresp.StatusCode == HttpStatusCode.Forbidden))
        {
            dresp.Dispose();
            throw new InvalidOperationException("Git LFS object download requires Axion:Signals:GitHub:Token (PAT).");
        }

        dresp.EnsureSuccessStatusCode();

        var ds = await dresp.Content.ReadAsStreamAsync(ct);
        return new HttpResponseOwnedStream(dresp, ds);
    }

    /* ===================== Streams ===================== */

    private sealed class HttpResponseOwnedStream : Stream
    {
        private readonly HttpResponseMessage _resp;
        private readonly Stream _inner;

        public HttpResponseOwnedStream(HttpResponseMessage resp, Stream inner)
        {
            _resp = resp;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                try { _resp.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _pos;
        private readonly Stream _inner;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix ?? Array.Empty<byte>();
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = ReadPrefix(buffer, offset, count);
            if (n > 0) return n;
            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = ReadPrefix(buffer.Span);
            if (n > 0) return n;
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        private int ReadPrefix(byte[] buffer, int offset, int count)
        {
            if (_pos >= _prefix.Length) return 0;
            var take = Math.Min(count, _prefix.Length - _pos);
            Buffer.BlockCopy(_prefix, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }

        private int ReadPrefix(Span<byte> buffer)
        {
            if (_pos >= _prefix.Length) return 0;
            var take = Math.Min(buffer.Length, _prefix.Length - _pos);
            _prefix.AsSpan(_pos, take).CopyTo(buffer);
            _pos += take;
            return take;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
