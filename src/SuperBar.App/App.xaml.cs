using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SuperBar.ControlCenter;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using SuperBar.Infrastructure;
using SuperBar.Runtime;
using SuperBar.Widgets;
using SuperBar.Windows;
using SuperBar.Windows.Startup;

namespace SuperBar.App;

/// <summary>Composition root and application lifecycle (start-up and shutdown coordination).</summary>
public sealed partial class App : Application, IAppLifetime
{
    private static AppPaths? s_paths;
    private static Serilog.ILogger? s_logger;
    private static string[] s_args = [];
    private ServiceProvider? _services;
    private IUiDispatcher? _dispatcher;
    private ILogger<App>? _log;
    private bool _exitRequested;
    private bool _shutDown;

    public App()
    {
        InitializeComponent();

        // Explicit shutdown: closing the last window (e.g. the Control Center while
        // SuperBar is disabled) must NOT end the process. Only IAppLifetime.Shutdown ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        UnhandledException += OnUnhandledException;
        Instance = this;
    }

    /// <summary>The running application; also reachable after the message loop ended (for the final safety net).</summary>
    internal static App? Instance { get; private set; }

    internal static void Configure(AppPaths paths, Serilog.ILogger logger, string[] args)
    {
        s_paths = paths;
        s_logger = logger;
        s_args = args;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (s_paths is null || s_logger is null)
        {
            throw new InvalidOperationException("App.Configure must run before the application starts.");
        }

        try
        {
            // Captured on the UI thread: every service marshals onto it through IUiDispatcher.
            _dispatcher = new DispatcherQueueUiDispatcher(DispatcherQueue.GetForCurrentThread());

            _services = new ServiceCollection()
                .AddSingleton<IUiDispatcher>(_dispatcher)
                .AddSingleton<IAppLifetime>(this)
                .AddSuperBarInfrastructure(s_paths, s_logger)
                .AddSuperBarWindowsIntegration()
                .AddSuperBarWidgets()
                .AddSuperBarRuntime()
                .AddSuperBarControlCenter()
                .AddSingleton<IUpdateService, VelopackUpdateService>()
                // Only an installed SuperBar has a stable path to register; a development build registers nothing.
                .AddSingleton<IStartupRegistration>(sp => new RunKeyStartupRegistration(
                    sp.GetRequiredService<IUpdateService>().IsInstalled ? Environment.ProcessPath : null))
                .AddSingleton<StartWithWindowsSync>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

            _log = _services.GetRequiredService<ILogger<App>>();

            // A previous session that crashed may have left the native taskbar hidden.
            _services.GetRequiredService<INativeTaskbarController>().RecoverFromPreviousSession();

            _services.GetRequiredService<ITaskbarRuntimeController>().Enable();

            _services.GetRequiredService<StartWithWindowsSync>().Start();

            if (s_args.Contains("--control-center"))
            {
                _services.GetRequiredService<IControlCenterLauncher>().Show();
            }
        }
        catch (Exception ex)
        {
            // Never linger as an invisible process (the app only exits explicitly): restore and quit.
            Serilog.Log.Fatal(ex, "SuperBar failed to start");
            ShutdownServices();
            Exit();
        }
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _log?.LogInformation("Exit requested");

        // Deferred so the caller (a menu command, the runtime) unwinds before its services are disposed.
        if (_dispatcher is null || !_dispatcher.TryEnqueue(ExitNow))
        {
            ExitNow();
        }
    }

    private void ExitNow()
    {
        ShutdownServices();
        Exit(); // ends Application.Start in Program.Main
    }

    /// <summary>Disposes the runtime and every service: hooks are unhooked and the native taskbar is restored. Idempotent.</summary>
    internal void ShutdownServices()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
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

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the shell alive: a faulty widget or handler must not take the taskbar down.
        if (_log is not null)
        {
            _log.LogError(e.Exception, "Unhandled UI exception: {Message}", e.Message);
        }
        else
        {
            Serilog.Log.Error(e.Exception, "Unhandled UI exception: {Message}", e.Message);
        }

        e.Handled = true;
    }
}
