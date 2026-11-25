using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Shell;
using SuperBar.Windows.AppBar;
using SuperBar.Windows.Bluetooth;
using SuperBar.Windows.Media;
using SuperBar.Windows.Apps;
using SuperBar.Windows.Monitors;
using SuperBar.Windows.Shell;
using SuperBar.Windows.Taskbar;
using SuperBar.Windows.Windows;

namespace SuperBar.Windows;

public static class WindowsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Win32 shell integration. Services that own native windows or hooks must be
    /// resolved on the UI thread (they are resolved lazily by the taskbar runtime at start-up).
    /// </summary>
    public static IServiceCollection AddSuperBarWindowsIntegration(this IServiceCollection services)
    {
        services.AddSingleton<ShellMessageWindow>();
        services.AddSingleton<IShellEvents>(sp => sp.GetRequiredService<ShellMessageWindow>());
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IAppBarService, AppBarService>();
        services.AddSingleton<INativeTaskbarController, NativeTaskbarController>();
        services.AddSingleton<IWindowTracker, WindowTracker>();
        services.AddSingleton<IWindowSwitcher, WindowSwitcher>();
        services.AddSingleton<IAppLauncher, AppLauncher>();
        services.AddSingleton<IIconProvider, IconProvider>();
        services.AddSingleton<IShellCommands, ShellCommands>();
        services.AddSingleton<IWindowChrome, WindowChrome>();
        services.AddSingleton<IWindowThumbnailService, WindowThumbnailService>();
        services.AddSingleton<ICursorService, CursorService>();
        services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();

        // Created on first use (by the Now Playing widget), on the UI thread.
        services.AddSingleton<IMediaSessionService, GsmtcMediaSessionService>();

        // Stateless; queried from the thread pool by the Bluetooth Battery widget.
        services.AddSingleton<IBluetoothBatteryService, PnpBluetoothBatteryService>();
        return services;
    }

    /// <summary>
    /// Standalone recovery used by crash handlers and `--restore-taskbar`: needs no UI thread and no DI container.
    /// </summary>
    public static bool RestoreNativeTaskbar(ITaskbarRecoveryStore store, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        return TaskbarRecovery.Restore(store, loggerFactory.CreateLogger("SuperBar.TaskbarRecovery"));
    }
}
