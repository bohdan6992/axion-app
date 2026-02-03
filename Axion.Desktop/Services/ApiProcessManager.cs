using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

        // ✅ Local API base URL (uses the same Port you run the API on)
        public string BaseUrl => $"http://localhost:{Port}";

        // Optional: expose last health error for UI/logs
        public string? LastHealthError { get; private set; }

        // ✅ Canon locations (Variant 2)
        private static readonly string CanonAppDir = @"C:\Axion\App";
        private static readonly string CanonCacheDir = @"C:\Axion\Cache";

        // Log files (helps debug "it started but health failed")
        public string StdoutLogPath => Path.Combine(CanonCacheDir, "api_stdout.log");
        public string StderrLogPath => Path.Combine(CanonCacheDir, "api_stderr.log");

        public ApiProcessManager(int port = 5197)
        {
            Port = port;
        }

        public void Start()
        {
            if (IsRunning) return;

            var appDir = ResolveApiDir();
            var apiExe = Path.Combine(appDir, "TradingBridgeApi.exe");
            var apiDll = Path.Combine(appDir, "TradingBridgeApi.dll");

            if (!File.Exists(apiExe) && !File.Exists(apiDll))
                throw new FileNotFoundException(
                    $"TradingBridgeApi not found. Looked in: {appDir}",
                    File.Exists(apiExe) ? apiExe : apiDll);

            Directory.CreateDirectory(CanonCacheDir);

            // reset logs for this run
            SafeWriteAllText(StdoutLogPath, "");
            SafeWriteAllText(StderrLogPath, "");

            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(apiExe) ? apiExe : "dotnet",
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!File.Exists(apiExe))
            {
                // framework-dependent publish
                psi.ArgumentList.Add(apiDll);
            }

            // ✅ Bind API to the same BaseUrl we will call
            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;

            // NOTE: keep Development if you want swagger; Production disables swagger in your Program.cs
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            // Signals repo token is provided via env (do NOT bake into appsettings.json)
            var signalsToken = Secrets.TryReadGitHubTokenForSignalsRepo(App.Install);
            if (!string.IsNullOrWhiteSpace(signalsToken))
                psi.Environment["Axion__Signals__GitHub__Token"] = signalsToken;

            var p = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    SafeAppendLine(StdoutLogPath, e.Data!);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    SafeAppendLine(StderrLogPath, e.Data!);
            };

            if (!p.Start())
                throw new InvalidOperationException("Failed to start TradingBridgeApi process.");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            _proc = p;
            LastHealthError = null;
        }

        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                try { _proc!.CloseMainWindow(); } catch { /* ignore */ }

                // ✅ Give hosted services time to shutdown cleanly
                if (!_proc!.WaitForExit(8000))
                    _proc!.Kill(entireProcessTree: true);
            }
            catch
            {
                try { _proc?.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            finally
            {
                try { _proc?.CancelOutputRead(); } catch { /* ignore */ }
                try { _proc?.CancelErrorRead(); } catch { /* ignore */ }

                _proc?.Dispose();
                _proc = null;
            }
        }

        /// <summary>
        /// Wait until API responds 200 OK on /health.
        /// IMPORTANT: do NOT call Stop() just because this returns false.
        /// </summary>
        public async Task<bool> WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken ct)
        {
            // ✅ If the caller passes too small timeout, clamp it.
            if (timeout < TimeSpan.FromSeconds(20))
                timeout = TimeSpan.FromSeconds(20);

            var stopAt = DateTime.UtcNow + timeout;

            // ✅ Per-request timeout MUST be > 2s (your API can need a few seconds to warm up)
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            while (DateTime.UtcNow < stopAt)
            {
                ct.ThrowIfCancellationRequested();

                if (_proc is null || _proc.HasExited)
                {
                    LastHealthError = BuildExitErrorMessage();
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
                    // Common: connection refused until Kestrel binds the port, or timeout if startup is heavy
                    LastHealthError = ex.Message;
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            // If we timed out, also hint where to look
            LastHealthError = (LastHealthError ?? "Health timed out") +
                              $" | See logs: {StdoutLogPath} / {StderrLogPath}";
            return false;
        }

        /// <summary>
        /// Optional helper: read API version from /version.
        /// Returns null if unavailable.
        /// </summary>
        public async Task<string?> TryGetVersionAsync(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

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

        /* ===================== HELPERS ===================== */

        private static string ResolveApiDir()
        {
            // Canon: API is always deployed to C:\Axion\App
            if (Directory.Exists(CanonAppDir))
                return CanonAppDir;

            // Fallback (dev / unusual layouts)
            try
            {
                var fallback = App.Install.AppDir;
                if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                    return fallback;
            }
            catch
            {
                // ignore
            }

            // last resort: current directory
            return Environment.CurrentDirectory;
        }

        private string BuildExitErrorMessage()
        {
            var code = -1;
            try { if (_proc != null && _proc.HasExited) code = _proc.ExitCode; } catch { /* ignore */ }

            var errTail = SafeReadTail(StderrLogPath, 30);
            if (!string.IsNullOrWhiteSpace(errTail))
                return $"API process exited (code {code}). Last stderr lines:\n{errTail}";

            var outTail = SafeReadTail(StdoutLogPath, 30);
            if (!string.IsNullOrWhiteSpace(outTail))
                return $"API process exited (code {code}). Last stdout lines:\n{outTail}";

            return $"API process exited (code {code}). See logs: {StdoutLogPath} / {StderrLogPath}";
        }

        private static void SafeWriteAllText(string path, string text)
        {
            try { File.WriteAllText(path, text); } catch { /* ignore */ }
        }

        private static void SafeAppendLine(string path, string line)
        {
            try
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        private static string SafeReadTail(string path, int maxLines)
        {
            try
            {
                if (!File.Exists(path)) return "";

                var lines = File.ReadAllLines(path);
                if (lines.Length <= maxLines) return string.Join(Environment.NewLine, lines);

                var start = Math.Max(0, lines.Length - maxLines);
                return string.Join(Environment.NewLine, lines[start..]);
            }
            catch
            {
                return "";
            }
        }
    }
}
