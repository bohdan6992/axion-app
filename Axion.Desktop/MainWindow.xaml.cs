using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Axion.Desktop.Services;

namespace Axion.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly ApiProcessManager _api;
        private readonly LoginService _login;
        private readonly GitHubReleaseAppUpdater _appUpdater;

        private CancellationTokenSource? _updateCheckCts;
        private CancellationTokenSource? _refreshLoopCts;

        // GitHub repo for APP-A updates
        private const string AppOwner = "bohdan6992";
        private const string AppRepo = "axion-app";
        private const string ApiAssetName = "TradingBridgeApi-win-x64.zip";

        public MainWindow()
        {
            InitializeComponent();

            _api = new ApiProcessManager(port: 5127);
            _login = new LoginService(_api.BaseUrl);

            _appUpdater = new GitHubReleaseAppUpdater(
                owner: AppOwner,
                repo: AppRepo,
                assetName: ApiAssetName,
                tokenProvider: Secrets.TryReadGitHubTokenForAppRepo);

            Loaded += (_, __) =>
            {
                RefreshStatus();

                // Fire-and-forget update badge check (safe)
                _ = CheckForUpdateBadgeAsync();

                // Periodic refresh (very light)
                StartRefreshLoop();
            };

            Closed += (_, __) =>
            {
                try { _updateCheckCts?.Cancel(); } catch { }
                try { _refreshLoopCts?.Cancel(); } catch { }
                try { _api.Stop(); } catch { }
            };
        }

        /* ===================== TITLE BAR (Variant B) ===================== */

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            // double-click => maximize/restore
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            try { DragMove(); } catch { /* ignore */ }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        /* ===================== LOG ===================== */

        private void AppendLog(string s)
        {
            try
            {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
                LogBox.ScrollToEnd();
            }
            catch
            {
                // ignore UI race conditions on shutdown
            }
        }

        /* ===================== STATUS ===================== */

        private void RefreshStatus()
        {
            // API tile
            ApiText.Text = $"API: {(_api.IsRunning ? "running" : "stopped")} ({_api.BaseUrl})";

            // Top pill status (right)
            StatusText.Text = _api.IsRunning ? "Running" : "Stopped";
            ApiDot.Fill = GetBrush(_api.IsRunning ? "GoodGreen" : "DangerRed");

            // Version tile
            var ver = ReadAppVersion() ?? "unknown";
            VersionText.Text = $"Version: {ver}";

            // Signals tile
            SignalsText.Text = $"Signals: {ReadSignalsRepo() ?? "unknown"}";
        }

        private void StartRefreshLoop()
        {
            _refreshLoopCts?.Cancel();
            _refreshLoopCts = new CancellationTokenSource();
            var ct = _refreshLoopCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(1500, ct);
                        await Dispatcher.InvokeAsync(RefreshStatus);
                    }
                }
                catch { /* ignore */ }
            });
        }

        private string? ReadAppVersion()
        {
            try
            {
                var p = Path.Combine(App.Install.CacheDir, "app_version.json");
                if (!File.Exists(p)) return null;

                using var fs = File.OpenRead(p);
                using var doc = JsonDocument.Parse(fs);

                if (doc.RootElement.TryGetProperty("tag", out var tag))
                    return tag.GetString();

                return null;
            }
            catch
            {
                return null;
            }
        }

        private string? ReadSignalsRepo()
        {
            try
            {
                // Best-effort: read from API appsettings.json in C:\Axion\App
                var p = Path.Combine(App.Install.AppDir, "appsettings.json");
                if (!File.Exists(p)) return null;

                using var fs = File.OpenRead(p);
                using var doc = JsonDocument.Parse(fs);

                var gh = doc.RootElement
                    .GetProperty("Axion")
                    .GetProperty("Signals")
                    .GetProperty("GitHub");

                var owner = gh.GetProperty("Owner").GetString();
                var repo = gh.GetProperty("Repo").GetString();
                var br = gh.GetProperty("Branch").GetString();

                // token presence: from appsettings OR from Secrets\signals_github_pat.txt
                string? tokenFromAppsettings = null;
                try { tokenFromAppsettings = gh.GetProperty("Token").GetString(); } catch { /* ignore */ }

                var hasTok =
                    !string.IsNullOrWhiteSpace(tokenFromAppsettings) ||
                    !string.IsNullOrWhiteSpace(Secrets.TryReadGitHubTokenForSignalsRepo(App.Install));

                return $"{owner}/{repo}@{br} | token={(hasTok ? "YES" : "NO")}";
            }
            catch
            {
                return null;
            }
        }

        private Brush GetBrush(string key)
        {
            try
            {
                if (Application.Current?.Resources.Contains(key) == true)
                    return (Brush)Application.Current.Resources[key];
            }
            catch { /* ignore */ }

            return Brushes.Gray;
        }

        /* ===================== UPDATE BADGE ===================== */

        private async Task CheckForUpdateBadgeAsync()
        {
            _updateCheckCts?.Cancel();
            _updateCheckCts = new CancellationTokenSource();
            var ct = _updateCheckCts.Token;

            try
            {
                SetUpdateUi("Checking releases…", "MutedText", showDot: false);

                var latest = await GetLatestReleaseTagAsync(ct);
                var current = ReadAppVersion() ?? "unknown";

                if (!string.IsNullOrWhiteSpace(latest) &&
                    !string.Equals(latest, "unknown", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase))
                {
                    SetUpdateUi($"Update available: {latest} (installed {current})", "AccentOrange", showDot: true);
                }
                else
                {
                    SetUpdateUi($"Up-to-date: {current}", "MutedText", showDot: false);
                }
            }
            catch
            {
                SetUpdateUi("Update check failed", "MutedText", showDot: false);
            }
        }

        private void SetUpdateUi(string text, string colorKey, bool showDot)
        {
            try
            {
                UpdateDot.Visibility = showDot ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { /* ignore */ }

            try
            {
                UpdateText.Text = text;
                UpdateText.Foreground = GetBrush(colorKey);
            }
            catch { /* ignore */ }

            // optional right tile (if exists in your XAML)
            try
            {
                UpdatesSummaryText.Text = text.StartsWith("Up-to-date", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : "Update available";
            }
            catch { /* ignore */ }
        }

        private async Task<string?> GetLatestReleaseTagAsync(CancellationToken ct)
        {
            var tok = Secrets.TryReadGitHubTokenForAppRepo(App.Install);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AxionLauncher/1.0");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            if (!string.IsNullOrWhiteSpace(tok))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok.Trim());

            var url = $"https://api.github.com/repos/{AppOwner}/{AppRepo}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                return tag.GetString();

            return null;
        }

        /* ===================== BUTTONS ===================== */

        private async void StartApi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("Starting API...");
                _api.Start();

                var ok = await _api.WaitUntilHealthyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                if (ok) AppendLog("API is up (health OK).");
                else AppendLog("API started but health-check failed: " + (_api.LastHealthError ?? "unknown"));
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

                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
                {
                    AppendLog("Login: email/password is empty.");
                    return;
                }

                AppendLog("Logging in...");
                var ok = await _login.LoginAsync(email, pass, CancellationToken.None);
                AppendLog(ok ? $"Login OK ({_login.Email})." : "Login failed.");

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
                var ok = await _api.WaitUntilHealthyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                AppendLog(ok ? "API is up (health OK)." : "API started but health-check failed: " + (_api.LastHealthError ?? "unknown"));

                RefreshStatus();

                _ = CheckForUpdateBadgeAsync();
            }
            catch (Exception ex)
            {
                AppendLog("Update App error: " + ex.Message);

                try
                {
                    AppendLog("Attempting rollback...");
                    GitHubReleaseAppUpdater.TryRollbackLastBackup(
                        App.Install,
                        new Progress<string>(s => AppendLog("Rollback: " + s)));
                }
                catch (Exception rb)
                {
                    AppendLog("Rollback failed: " + rb.Message);
                }

                RefreshStatus();
                _ = CheckForUpdateBadgeAsync();
            }
            finally
            {
                UpdateAppBtn.IsEnabled = true;
            }
        }
    }
}
