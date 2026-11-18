using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperBar.ControlCenter;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using SuperBar.Infrastructure;
using SuperBar.Runtime;
using SuperBar.Widgets;
using SuperBar.Windows;

namespace SuperBar.App;

/// <summary>Composition root and application lifecycle (start-up and shutdown coordination).</summary>
public sealed class App : Application
{
    private static AppPaths? s_paths;
    private static Serilog.ILogger? s_logger;
    private static string[] s_args = [];
    private ServiceProvider? _services;
    private ILogger<App>? _log;

    internal static void Configure(AppPaths paths, Serilog.ILogger logger, string[] args)
    {
        s_paths = paths;
        s_logger = logger;
        s_args = args;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && s_paths is not null && s_logger is not null)
        {
            _services = new ServiceCollection()
                .AddSuperBarInfrastructure(s_paths, s_logger)
                .AddSuperBarWindowsIntegration()
                .AddSuperBarWidgets()
                .AddSuperBarRuntime()
                .AddSuperBarControlCenter()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

            _log = _services.GetRequiredService<ILogger<App>>();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                // Keep the shell alive: a faulty widget or handler must not take the taskbar down.
                _log.LogError(e.Exception, "Unhandled UI exception");
                e.Handled = true;
            };

            desktop.Exit += (_, _) => Shutdown();

            // A previous session that crashed may have left the native taskbar hidden.
            _services.GetRequiredService<INativeTaskbarController>().RecoverFromPreviousSession();

            _services.GetRequiredService<ITaskbarRuntimeController>().Enable();

            if (s_args.Contains("--control-center"))
            {
                _services.GetRequiredService<IControlCenterLauncher>().Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        _log?.LogInformation("Shutting down");
        try
        {
            _services?.GetService<TaskbarRuntime>()?.Dispose();
            _services?.Dispose(); // disposes hooks, shell windows and restores the native taskbar
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error during shutdown");
        }
    }
}
