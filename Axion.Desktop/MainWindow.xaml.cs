using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using Axion.Desktop.Services;

namespace Axion.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly ApiProcessManager _api;
        private readonly LoginService _login;
        private readonly GitHubReleaseAppUpdater _appUpdater;

        public MainWindow()
        {
            InitializeComponent();

            _api = new ApiProcessManager(port: 5127);

            // На Етапі 1 логін поки викликає /api/auth/login (з’явиться на Етапі 2).
            // Але UI/flow ми вже готуємо.
            _login = new LoginService(_api.BaseUrl);

            _appUpdater = new GitHubReleaseAppUpdater(
                owner: "bohdan6992",
                repo: "axion-app",
                assetName: "TradingBridgeApi-win-x64.zip",
                tokenProvider: Secrets.TryReadGitHubTokenForAppRepo);

            RefreshStatus();
        }

        private void AppendLog(string s)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
            LogBox.ScrollToEnd();
        }

        private void RefreshStatus()
        {
            ApiText.Text = $"API: {(_api.IsRunning ? "running" : "stopped")} ({_api.BaseUrl})";
            StatusText.Text = $"Status: {(_login.Jwt is null ? "Not logged in" : $"Logged in ({_login.Email})")}";
            VersionText.Text = $"Version: {ReadAppVersion() ?? "unknown"}";
            SignalsText.Text = $"Signals: {ReadSignalsRepo() ?? "unknown"}";
        }

        private string? ReadAppVersion()
        {
            try
            {
                var p = Path.Combine(App.Install.CacheDir, "app_version.json");
                if (!File.Exists(p)) return null;
                using var fs = File.OpenRead(p);
                var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("tag", out var tag))
                    return tag.GetString();
            }
            catch { }
            return null;
        }

        private string? ReadSignalsRepo()
        {
            try
            {
                // Read from API appsettings (best-effort): C:\Axion\App\appsettings.json
                var p = Path.Combine(App.Install.AppDir, "appsettings.json");
                if (!File.Exists(p)) return null;
                using var fs = File.OpenRead(p);
                var doc = JsonDocument.Parse(fs);
                var gh = doc.RootElement
                    .GetProperty("Axion")
                    .GetProperty("Signals")
                    .GetProperty("GitHub");
                var owner = gh.GetProperty("Owner").GetString();
                var repo = gh.GetProperty("Repo").GetString();
                var br = gh.GetProperty("Branch").GetString();
                var tokenFromAppsettings = gh.GetProperty("Token").GetString();
                var hasTok = !string.IsNullOrWhiteSpace(tokenFromAppsettings)
                             || !string.IsNullOrWhiteSpace(Secrets.TryReadGitHubTokenForSignalsRepo(App.Install));
                return $"{owner}/{repo}@{br} | token={(hasTok ? "YES" : "NO")}";
            }
            catch { return null; }
        }

        private async void StartApi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("Starting API...");
                _api.Start();

                var ok = await _api.WaitUntilHealthyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                AppendLog(ok ? "API is up." : "API started but health-check failed (will be fixed on API этапі).");
            }
            catch (Exception ex)
            {
                AppendLog("Start API error: " + ex.Message);
            }
            finally
            {
                RefreshStatus();
            }
        }

        private void StopApi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("Stopping API...");
                _api.Stop();
                AppendLog("API stopped.");
            }
            catch (Exception ex)
            {
                AppendLog("Stop API error: " + ex.Message);
            }
            finally
            {
                RefreshStatus();
            }
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_api.IsRunning)
                {
                    AppendLog("API is not running. Starting API first...");
                    _api.Start();
                    await _api.WaitUntilHealthyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }

                var email = EmailBox.Text?.Trim() ?? "";
                var pass = PasswordBox.Password ?? "";

                AppendLog("Logging in...");
                var ok = await _login.LoginAsync(email, pass, CancellationToken.None);
                AppendLog(ok ? "Login OK." : "Login failed.");

                RefreshStatus();
            }
            catch (Exception ex)
            {
                AppendLog("Login error: " + ex.Message);
            }
        }

        private void OpenSwagger_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = $"{_api.BaseUrl}/swagger";
                AppendLog("Opening Swagger: " + url);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("Open Swagger error: " + ex.Message);
            }
        }

        private async void UpdateApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateAppBtn.IsEnabled = false;
                AppendLog("Updating app from GitHub Releases...");

                // Canon: atomic update only affects C:\Axion\App (API binaries). Signals repo is never touched.
                if (_api.IsRunning)
                {
                    AppendLog("Stopping API for update...");
                    _api.Stop();
                }

                var prog = new Progress<string>(s => AppendLog("Update App: " + s));
                var result = await _appUpdater.UpdateApiAsync(App.Install, prog, CancellationToken.None);
                AppendLog(result.Updated
                    ? $"Updated to {result.Tag}. Starting API..."
                    : $"No updates. Current={result.Tag}. Starting API...");

                _api.Start();
                await _api.WaitUntilHealthyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                AppendLog("Update App error: " + ex.Message);
                try
                {
                    AppendLog("Attempting rollback...");
                    GitHubReleaseAppUpdater.TryRollbackLastBackup(App.Install, new Progress<string>(s => AppendLog("Rollback: " + s)));
                }
                catch (Exception rb)
                {
                    AppendLog("Rollback failed: " + rb.Message);
                }
            }
            finally
            {
                UpdateAppBtn.IsEnabled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _api.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
