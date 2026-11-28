using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using SuperBar.Infrastructure;
using SuperBar.Infrastructure.Logging;
using SuperBar.Infrastructure.Persistence;
using SuperBar.Windows;
using SuperBar.Windows.Startup;

namespace SuperBar.App;

/// <summary>
/// What SuperBar does when the installer is about to replace or remove it. Velopack starts a short-lived
/// SuperBar.exe for each hook (no UI; it is killed after 15 to 30 seconds) and exits the process afterwards.
///
/// The updater first waits for the running SuperBar to exit and force-stops it if it does not; the uninstaller
/// force-stops it right away. A force-stopped SuperBar cannot restore the native taskbar, so both hooks do it from
/// the recovery record: the user is never left without a taskbar.
/// </summary>
internal static class InstallerHooks
{
    /// <summary>Runs in the old version, before its files are replaced.</summary>
    public static void BeforeUpdate() => Run("update", removeStartupEntry: false);

    /// <summary>Runs before the application folder is removed.</summary>
    public static void BeforeUninstall() => Run("uninstall", removeStartupEntry: true);

    private static void Run(string operation, bool removeStartupEntry)
    {
        SerilogLoggerFactory? loggerFactory = null;
        try
        {
            var paths = new AppPaths();
            loggerFactory = new SerilogLoggerFactory(LoggingConfiguration.CreateLogger(paths.LogDirectory), dispose: true);
            var log = loggerFactory.CreateLogger("SuperBar.InstallerHooks");

            // Each step stands alone: a failure in one must not skip the other.
            try
            {
                var restored = WindowsServiceCollectionExtensions.RestoreNativeTaskbar(new JsonTaskbarRecoveryStore(paths.RecoveryFilePath), loggerFactory);
                log.LogInformation("Before {Operation}: native taskbar restored = {Restored}", operation, restored);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Before {Operation}: restoring the native taskbar failed", operation);
            }

            if (removeStartupEntry)
            {
                try
                {
                    new RunKeyStartupRegistration(executablePath: null).SetEnabled(false);
                    log.LogInformation("Before {Operation}: removed the 'Start with Windows' entry", operation);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Before {Operation}: removing the 'Start with Windows' entry failed", operation);
                }
            }
        }
        catch (Exception)
        {
            // A hook must never fail the installer; there is nowhere left to report this.
        }
        finally
        {
            loggerFactory?.Dispose();
        }
    }
}
