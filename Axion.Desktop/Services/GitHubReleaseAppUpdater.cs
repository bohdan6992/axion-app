using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Axion.Desktop.Services
{
    public sealed class ApiProcessManager
    {
        private Process? _proc;

        public bool IsRunning => _proc is { HasExited: false };
        public int Port { get; }
        public string BaseUrl => $"http://localhost:{Port}";
        public string? LastHealthError { get; private set; }

        // Where we log API process output (VERY useful)
        public string StdOutLogPath { get; }
        public string StdErrLogPath { get; }

        public ApiProcessManager(int port = 5197)
        {
            Port = port;

            // Prefer Axion cache dir if available, else fallback to C:\Axion\Cache
            var cacheDir =
                TryGetAxionCacheDir() ??
                @"C:\Axion\Cache";

            Directory.CreateDirectory(cacheDir);
            StdOutLogPath = Path.Combine(cacheDir, "api_stdout.log");
            StdErrLogPath = Path.Combine(cacheDir, "api_stderr.log");
        }

        public void Start()
        {
            if (IsRunning) return;

            // If port is already open, don't spawn duplicates (launcher might be racing)
            if (IsPortOpen("127.0.0.1", Port, TimeSpan.FromMilliseconds(250)))
            {
                LastHealthError = null;
                return;
            }

            var apiDir = ResolveApiDir();
            var apiExe = Path.Combine(apiDir, "TradingBridgeApi.exe");
            var apiDll = Path.Combine(apiDir, "TradingBridgeApi.dll");

            if (!File.Exists(apiExe) && !File.Exists(apiDll))
                throw new FileNotFoundException(
                    $"TradingBridgeApi not found. Looked in: {apiDir}",
                    File.Exists(apiExe) ? apiExe : apiDll);

            // Clear old logs on each start (optional, but makes it readable)
            TryWriteText(StdOutLogPath, $"[{DateTime.Now:O}] === START ===\n");
            TryWriteText(StdErrLogPath, $"[{DateTime.Now:O}] === START ===\n");

            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(apiExe) ? apiExe : "dotnet",
                WorkingDirectory = apiDir,

                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!File.Exists(apiExe))
            {
                // framework-dependent publish
                psi.ArgumentList.Add(apiDll);
            }

            // Bind to the same BaseUrl we will call
            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;

            // IMPORTANT: don't force Development if you're shipping Production builds;
            // but leaving it is fine if you want Swagger/etc. Just keep consistent.
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

            // Signals repo token is provided via env
            var signalsToken = Secrets.TryReadGitHubTokenForSignalsRepo(App.Install);
            if (!string.IsNullOrWhiteSpace(signalsToken))
                psi.Environment["Axion__Signals__GitHub__Token"] = signalsToken;

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    TryAppendLine(StdOutLogPath, e.Data);
            };

            _proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    TryAppendLine(StdErrLogPath, e.Data);
            };

            if (!_proc.Start())
                throw new InvalidOperationException("Failed to start TradingBridgeApi process.");

            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            LastHealthError = null;
        }

        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                // API is console app => CloseMainWindow often does nothing
                // Kill is the reliable way
                _proc!.Kill(entireProcessTree: true);
                _proc.WaitForExit(1500);
            }
            catch
            {
                try { _proc?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                _proc?.Dispose();
                _proc = null;
            }
        }

        /// <summary>
        /// Wait until API responds 200 OK on /health.
        /// Also detects "process died immediately" and returns a meaningful error.
        /// </summary>
        public async Task<bool> WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken ct)
        {
            var stopAt = DateTime.UtcNow + timeout;

            // Per-request timeout; overall timeout is the loop.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            while (DateTime.UtcNow < stopAt)
            {
                ct.ThrowIfCancellationRequested();

                // If we started a process and it already exited — this is NOT "slow start"
                if (_proc is not null && _proc.HasExited)
                {
                    var tail = SafeTail(StdErrLogPath, 40);
                    LastHealthError =
                        $"API process exited (code={_proc.ExitCode}). " +
                        $"See stderr log: {StdErrLogPath}\n" +
                        (string.IsNullOrWhiteSpace(tail) ? "" : $"--- stderr tail ---\n{tail}");
                    return false;
                }

                try
                {
                    var resp = await http.GetAsync($"{BaseUrl}/health", ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        LastHealthError = null;
                        return true;
                    }

                    LastHealthError = $"GET /health -> {(int)resp.StatusCode} {resp.ReasonPhrase}";
                }
                catch (Exception ex)
                {
                    LastHealthError = ex.Message;
                }

                await Task.Delay(250, ct);
            }

            // Final diagnostic if port never opened
            if (!IsPortOpen("127.0.0.1", Port, TimeSpan.FromMilliseconds(200)))
            {
                LastHealthError =
                    $"Port {Port} is not listening. " +
                    $"If API was started, check logs:\nstdout: {StdOutLogPath}\nstderr: {StdErrLogPath}";
            }

            return false;
        }

        public async Task<string?> TryGetVersionAsync(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            try
            {
                var resp = await http.GetAsync($"{BaseUrl}/version", ct);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();

                return null;
            }
            catch
            {
                return null;
            }
        }

        // -------------------- helpers --------------------

        private static string? TryGetAxionCacheDir()
        {
            try
            {
                // if your InstallLayout exposes CacheDir, use it
                return App.Install?.CacheDir;
            }
            catch { return null; }
        }

        private static bool IsPortOpen(string host, int port, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using var client = new TcpClient();
                var t = client.ConnectAsync(host, port, cts.Token);
                t.GetAwaiter().GetResult();
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static void TryWriteText(string path, string text)
        {
            try { File.WriteAllText(path, text); } catch { }
        }

        private static void TryAppendLine(string path, string line)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
        }

        private static string SafeTail(string path, int maxLines)
        {
            try
            {
                if (!File.Exists(path)) return "";
                var lines = File.ReadLines(path).Reverse().Take(maxLines).Reverse();
                return string.Join(Environment.NewLine, lines);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveApiDir()
        {
            // 1) Canon: C:\Axion\App
            var p1 = @"C:\Axion\App";
            if (LooksLikeApiDir(p1)) return p1;

            // 2) If launcher has a root folder concept, try root\App
            try
            {
                var root = App.Install?.RootDir; // if exists in your InstallLayout
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var p2 = Path.Combine(root, "App");
                    if (LooksLikeApiDir(p2)) return p2;
                }
            }
            catch { /* ignore */ }

            // 3) Fallback: old behavior (whatever AppDir was)
            var appDir = App.Install.AppDir;
            if (LooksLikeApiDir(appDir)) return appDir;

            // 4) Last resort: return canonical even if missing (so error message is clear)
            return p1;
        }

        private static bool LooksLikeApiDir(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
                return File.Exists(Path.Combine(dir, "TradingBridgeApi.exe")) ||
                       File.Exists(Path.Combine(dir, "TradingBridgeApi.dll"));
            }
            catch
            {
                return false;
            }
        }
    }
}
