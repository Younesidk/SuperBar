using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Extensions.Logging;
using SuperBar.Infrastructure;
using SuperBar.Infrastructure.Logging;
using SuperBar.Infrastructure.Persistence;
using SuperBar.Windows;
using Velopack;

namespace SuperBar.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\SuperBar.SingleInstance";

    /// <summary>
    /// Command line:
    ///   --restore-taskbar   restore a native taskbar left hidden by a crashed session, then exit
    ///   --control-center    open the Control Center on start-up
    ///   --verbose           debug-level logging
    /// The XAML-generated Main is disabled (DISABLE_XAML_GENERATED_MAIN) so all of this runs before WinUI starts.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and exits when appropriate.
        VelopackApp.Build().Run();

        var paths = new AppPaths();
        Log.Logger = LoggingConfiguration.CreateLogger(paths.LogDirectory, verbose: args.Contains("--verbose"));
        using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
        var recoveryStore = new JsonTaskbarRecoveryStore(paths.RecoveryFilePath);
        void EmergencyRestore() => WindowsServiceCollectionExtensions.RestoreNativeTaskbar(recoveryStore, loggerFactory);

        try
        {
            if (args.Contains("--restore-taskbar"))
            {
                // No WinUI: works even when the UI cannot start.
                Log.Information("--restore-taskbar: restored = {Restored}", WindowsServiceCollectionExtensions.RestoreNativeTaskbar(recoveryStore, loggerFactory));
                return 0;
            }

            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                // Two bars would fight over the AppBar slot and the native taskbar state.
                Log.Warning("SuperBar is already running; exiting");
                return 1;
            }

            Log.Information("SuperBar {Version} starting (pid {Pid}, {Os})", typeof(Program).Assembly.GetName().Version, Environment.ProcessId, Environment.OSVersion);

            // Last-resort safety nets: never leave the user without a taskbar.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; restoring native taskbar");
                EmergencyRestore();
                Log.CloseAndFlush();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyRestore();

            App.Configure(paths, Log.Logger, args);

            // Manual WinUI 3 bootstrap (what the generated Main would do), on this STA thread.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callbackParams =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });

            // The message loop has ended (normally via App.Shutdown). Make sure services are disposed either way.
            App.Instance?.ShutdownServices();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "SuperBar terminated unexpectedly");
            return -1;
        }
        finally
        {
            EmergencyRestore();
            Log.Information("SuperBar stopped");
            Log.CloseAndFlush();
        }
    }
}
