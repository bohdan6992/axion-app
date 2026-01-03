using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Axion.Desktop.Services
{
    public sealed class ApiProcessManager
    {
        private Process? _proc;

        public bool IsRunning => _proc is { HasExited: false };
        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public ApiProcessManager(int port = 5127)
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

            // signals root у %AppData%\Axion\signals — віддаємо через env (або args, якщо ви так зробите в API на Етапі 2)
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

            // Old port + always-swagger (Development)
            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            // Signals repo token is provided via env (do NOT bake into appsettings.json)
            var signalsToken = Secrets.TryReadGitHubTokenForSignalsRepo(App.Install);
            if (!string.IsNullOrWhiteSpace(signalsToken))
                psi.Environment["Axion__Signals__GitHub__Token"] = signalsToken;

            _proc = Process.Start(psi);
            if (_proc is null) throw new InvalidOperationException("Failed to start TradingBridgeApi process.");
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

        public async Task<bool> WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken ct)
        {
            var stopAt = DateTime.UtcNow + timeout;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            while (DateTime.UtcNow < stopAt)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // health endpoint додамо в API на Етапі 2, але на старті можна пінгувати /
                    var resp = await http.GetAsync($"{BaseUrl}/", ct);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { /* ignore */ }

                await Task.Delay(250, ct);
            }
            return false;
        }
    }
}
