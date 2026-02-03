using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

        // ✅ Default to the requested local port
        public ApiProcessManager(int port = 5197)
        {
            Port = port;
        }

        public void Start()
        {
            if (IsRunning) return;

            // Canon (Variant 2): API lives under C:\Axion\App\ and is updated atomically.
            // Launcher lives under C:\Axion\Launcher\.
            var appDir = App.Install.AppDir;
            var apiExe = Path.Combine(appDir, "TradingBridgeApi.exe");
            var apiDll = Path.Combine(appDir, "TradingBridgeApi.dll");

            if (!File.Exists(apiExe) && !File.Exists(apiDll))
                throw new FileNotFoundException("TradingBridgeApi not found in C:\\Axion\\App", apiExe);

            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(apiExe) ? apiExe : "dotnet",
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!File.Exists(apiExe))
            {
                // framework-dependent publish
                psi.ArgumentList.Add(apiDll);
            }

            // ✅ Bind API to the same BaseUrl we will call
            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            // Signals repo token is provided via env (do NOT bake into appsettings.json)
            var signalsToken = Secrets.TryReadGitHubTokenForSignalsRepo(App.Install);
            if (!string.IsNullOrWhiteSpace(signalsToken))
                psi.Environment["Axion__Signals__GitHub__Token"] = signalsToken;

            _proc = Process.Start(psi);
            if (_proc is null) throw new InvalidOperationException("Failed to start TradingBridgeApi process.");

            LastHealthError = null;
        }

        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                _proc!.CloseMainWindow();
                if (!_proc.WaitForExit(1500))
                    _proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
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
        /// </summary>
        public async Task<bool> WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken ct)
        {
            var stopAt = DateTime.UtcNow + timeout;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            while (DateTime.UtcNow < stopAt)
            {
                ct.ThrowIfCancellationRequested();

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

            return false;
        }

        /// <summary>
        /// Optional helper: read API version from /version.
        /// Returns null if unavailable.
        /// </summary>
        public async Task<string?> TryGetVersionAsync(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

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
    }
}
