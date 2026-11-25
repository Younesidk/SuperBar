using Microsoft.Extensions.DependencyInjection;
using SuperBar.ControlCenter.ViewModels;
using SuperBar.ControlCenter.Views;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter;

/// <summary>Opens the (single) Control Center window. Separate from the taskbar runtime; it only edits settings.</summary>
public sealed class ControlCenterService(IServiceProvider services, IUiDispatcher dispatcher) : IControlCenterLauncher
{
    private ControlCenterWindow? _window;

    public void Show()
    {
        dispatcher.VerifyAccess();
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
            services.GetRequiredService<IAppLauncher>(),
            services.GetRequiredService<IIconProvider>(),
            dispatcher);

        var window = new ControlCenterWindow(vm, services.GetRequiredService<IWindowChrome>());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Activate();
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
