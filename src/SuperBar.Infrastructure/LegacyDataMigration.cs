namespace SuperBar.Infrastructure;

/// <summary>
/// Until 0.1.0 SuperBar kept its data in %LOCALAPPDATA%\SuperBar. That folder is now the install folder: the
/// installer replaces it on install and repair and removes it on uninstall, so data must not live there. This moves
/// the data files of a pre-installer build to the new data folder, once.
/// </summary>
public static class LegacyDataMigration
{
    private const string Database = "settings.db";

    // Only SuperBar's own files, by name: the folder may be an installation by now (current\, Update.exe, packages\).
    private static readonly string[] DataFiles =
    [
        Database, "settings.db-wal", "settings.db-shm", "settings.db-journal", "taskbar-recovery.json", "badges.json",
    ];

    /// <summary>
    /// Moves the data files from <paramref name="legacyDirectory"/> to <paramref name="dataDirectory"/> when the old
    /// folder has a settings database and the new one has none. Never throws; returns true when it moved the data.
    /// </summary>
    public static bool TryMove(string legacyDirectory, string dataDirectory)
    {
        try
        {
            if (!File.Exists(Path.Combine(legacyDirectory, Database)) || File.Exists(Path.Combine(dataDirectory, Database)))
            {
                return false;
            }

            Directory.CreateDirectory(dataDirectory);

            // The database goes first: if it is locked (an older SuperBar is still running), nothing has moved yet.
            foreach (var name in DataFiles)
            {
                var source = Path.Combine(legacyDirectory, name);
                var target = Path.Combine(dataDirectory, name);
                if (File.Exists(source) && !File.Exists(target))
                {
                    File.Move(source, target);
                }
            }

            var logs = Path.Combine(legacyDirectory, "logs");
            var newLogs = Path.Combine(dataDirectory, "logs");
            if (Directory.Exists(logs) && !Directory.Exists(newLogs))
            {
                Directory.Move(logs, newLogs);
            }

            // Leaves the installer a clean slate; fails (and is ignored) when anything else is still in the folder.
            try
            {
                Directory.Delete(legacyDirectory, recursive: false);
            }
            catch (IOException)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
