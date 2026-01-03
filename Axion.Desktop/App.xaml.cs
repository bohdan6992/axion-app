using System.Windows;
using Axion.Desktop.Services;

namespace Axion.Desktop;

public partial class App : Application
{
    public static InstallLayout Install { get; private set; } = InstallLayout.DetectFromLauncherLocation();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Install = InstallLayout.DetectFromLauncherLocation();
        Install.EnsureAll();

        // TODO: Initialize AppData folders, load config
        // TODO: Optional: Quick Update Data (non-blocking)
        // TODO: Show login window first; start local API only after successful login
        await Task.CompletedTask;
    }
}
