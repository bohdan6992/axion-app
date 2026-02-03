using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Axion.Desktop.Services
{
    public sealed class ApiProcessManager
    {
        private Process? _proc;
        private bool _attachedToExisting; // if we detected an already-running API and didn't spawn it

        public bool IsRunning => _proc is { HasExited: false } || _attachedToExisting;
        public int Port { get; }
        public string BaseUrl => $"http://localhost:{Port}";
        public string? LastHealthError { get; private set; }

        // Where we log API process output (VERY useful)
        public string StdOutLogPath { get; }
        public string StdErrLogPath { get; }

        public ApiProcessManager(int port = 5197)
        {
            Port = port;

            var cacheDir = TryGetAxionCacheDir() ?? @"C:\Axion\Cache";
            Directory.CreateDirectory(cacheDir);

            StdOutLogPath = Path.Combine(cacheDir, "api_stdout.log");
            StdErrLogPath = Path.Combine(cacheDir, "api_stderr.log");
        }

        public void Start()
        {
            // If we already spawned it and it's alive — nothing to do
            if (_proc is { HasExited: false }) return;

            // If API is already up (maybe started by another launcher instance) — don't spawn duplicates
            if (IsHealthyNow())
            {
                _attachedToExisting = true;
                LastHealthError = null;
                return;
            }

            _attachedToExisting = false;

            var apiDir = ResolveApiDir();
            var apiExe = Path.Combine(apiDir, "TradingBridgeApi.exe");
            var apiDll = Path.Combine(apiDir, "TradingBridgeApi.dll");

            if (!File.Exists(apiExe) && !File.Exists(apiDll))
                throw new FileNotFoundException($"TradingBridgeApi not found. Looked in: {apiDir}",
                    File.Exists(apiExe) ? apiExe : apiDll);

            // Reset logs on each start
            TryWriteText(StdOutLogPath, $"[{DateTime.Now:O}] === START ==={Environment.NewLine}");
            TryWriteText(StdErrLogPath, $"[{DateTime.Now:O}] === START ==={Environment.NewLine}");

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

            // Keep consistent with your API config:
            // If Swagger is disabled in Production in Program.cs, set Development here.
            // Otherwise keep Production. Pick ONE.
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

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
            // If we didn't spawn it (we just "attached" to an existing API), don't kill someone else's process
            if (_attachedToExisting)
            {
                _attachedToExisting = false;
                LastHealthError = null;
                return;
            }

            if (_proc is null || _proc.HasExited) return;

            try
            {
                // Best-effort graceful first
                try { _proc.CloseMainWindow(); } catch { }

                // Give hosted services time to shut down
                if (!_proc.WaitForExit(8000))
                    _proc.Kill(entireProcessTree: true);
            }
            catch
            {
                try { _proc?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                try { _proc?.CancelOutputRead(); } catch { }
                try { _proc?.CancelErrorRead(); } catch { }
                _proc?.Dispose();
                _proc = null;
            }
        }

        /// <summary>
        /// Wait until API responds 200 OK on /health.
        /// Also detects "process died immediately" and returns a meaningful error.
        /// IMPORTANT: do NOT call Stop() just because this returns false.
        /// </summary>
        public async Task<bool> WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken ct)
        {
            // Clamp too-small timeouts (your UI sometimes passed 2s earlier)
            if (timeout < TimeSpan.FromSeconds(15))
                timeout = TimeSpan.FromSeconds(15);

            var stopAt = DateTime.UtcNow + timeout;

            // Per-request timeout; overall timeout is the loop.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            while (DateTime.UtcNow < stopAt)
            {
                ct.ThrowIfCancellationRequested();

                // If we started a process and it already exited — this is NOT "slow start"
                if (_proc is not null && _proc.HasExited)
                {
                    var tail = SafeTail(StdErrLogPath, 80);
                    LastHealthError =
                        $"API process exited (code={_proc.ExitCode}). " +
                        $"See stderr log: {StdErrLogPath}\n" +
                        (string.IsNullOrWhiteSpace(tail) ? "" : $"--- stderr tail ---\n{tail}");
                    return false;
                }

                try
                {
                    using var resp = await http.GetAsync($"{BaseUrl}/health", ct).ConfigureAwait(false);
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

                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            // Final diagnostic if still not healthy
            if (!IsHealthyNow())
            {
                LastHealthError =
                    $"API not healthy within {timeout.TotalSeconds:0}s. " +
                    $"Check logs:\nstdout: {StdOutLogPath}\nstderr: {StdErrLogPath}";
            }

            return false;
        }

        public async Task<string?> TryGetVersionAsync(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            try
            {
                var resp = await http.GetAsync($"{BaseUrl}/version", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

        private bool IsHealthyNow()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(350) };
                using var resp = http.GetAsync($"{BaseUrl}/health").GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetAxionCacheDir()
        {
            try
            {
                return App.Install?.CacheDir;
            }
            catch
            {
                return null;
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
