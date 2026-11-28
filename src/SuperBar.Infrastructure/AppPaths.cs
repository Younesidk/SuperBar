using SuperBar.Core.Runtime;

namespace SuperBar.Infrastructure;

/// <summary>
/// All SuperBar data lives under %LOCALAPPDATA%\SuperBarData (per user, never roaming, never in the install folder).
/// It is not %LOCALAPPDATA%\SuperBar: that is where the installer puts the application, and it replaces that folder
/// on install and repair and removes it on uninstall.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        if (dataDirectory is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dataDirectory = Path.Combine(localAppData, "SuperBarData");
            LegacyDataMigration.TryMove(Path.Combine(localAppData, "SuperBar"), dataDirectory);
        }

        DataDirectory = dataDirectory;
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "settings.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// JSON (not SQLite) on purpose: recovery must work even if the database is locked or corrupt,
    /// and a user can read or delete it by hand.
    /// </summary>
    public string RecoveryFilePath => Path.Combine(DataDirectory, "taskbar-recovery.json");

    /// <summary>App badges (overlay icons) kept across a restart of the bar.</summary>
    public string BadgesFilePath => Path.Combine(DataDirectory, "badges.json");
}
