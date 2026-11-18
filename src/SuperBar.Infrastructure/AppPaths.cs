using SuperBar.Core.Runtime;

namespace SuperBar.Infrastructure;

/// <summary>All SuperBar data lives under %LOCALAPPDATA%\SuperBar (per user, never roaming, never in the install folder).</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperBar");
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
}
