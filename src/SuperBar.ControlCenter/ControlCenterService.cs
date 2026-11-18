using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SuperBar.ControlCenter.ViewModels;
using SuperBar.ControlCenter.Views;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter;

/// <summary>Opens the (single) Control Center window. Separate from the taskbar runtime; it only edits settings.</summary>
public sealed class ControlCenterService(IServiceProvider services) : IControlCenterLauncher
{
    private ControlCenterWindow? _window;

    public void Show()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var vm = new ControlCenterViewModel(
            services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<ITaskbarRuntimeController>(),
            services.GetRequiredService<IWidgetRegistry>(),
            services.GetRequiredService<IAppPaths>(),
            services.GetRequiredService<IAppLauncher>());

        _window = new ControlCenterWindow { DataContext = vm };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }
}

public static class ControlCenterServiceCollectionExtensions
{
    public static IServiceCollection AddSuperBarControlCenter(this IServiceCollection services)
    {
        services.AddSingleton<IControlCenterLauncher, ControlCenterService>();
        return services;
    }
}
