using Microsoft.Win32;
using SuperBar.Infrastructure;
using SuperBar.Windows.Startup;

namespace SuperBar.Tests.Infrastructure;

/// <summary>What the installer relies on: the "Start with Windows" registration and the move out of the install folder.</summary>
public sealed class InstallSupportTests : IDisposable
{
    // A throwaway key: the tests never touch the real Run key.
    private readonly string _testKey = $@"Software\SuperBar.Tests.{Guid.NewGuid():N}";
    private readonly string _runKey;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SuperBar.Tests", Guid.NewGuid().ToString("N"));

    public InstallSupportTests() => _runKey = _testKey + @"\Run";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(RunKeyStartupRegistration.DefaultValueName) as string;
    }

    [Fact]
    public void Enabling_writes_the_quoted_executable_path_and_disabling_removes_it()
    {
        var exe = @"C:\Users\someone\AppData\Local\SuperBar\current\SuperBar.exe";
        var registration = new RunKeyStartupRegistration(exe, _runKey);
        Assert.True(registration.IsAvailable);
        Assert.False(registration.IsEnabled);

        registration.SetEnabled(true);
        Assert.Equal($"\"{exe}\"", RunValue());
        Assert.True(registration.IsEnabled);

        registration.SetEnabled(false);
        Assert.Null(RunValue());
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void A_registration_that_points_at_another_install_folder_is_not_enabled_and_is_rewritten()
    {
        new RunKeyStartupRegistration(@"D:\Old\SuperBar\current\SuperBar.exe", _runKey).SetEnabled(true);

        var moved = new RunKeyStartupRegistration(@"C:\New\SuperBar\current\SuperBar.exe", _runKey);
        Assert.False(moved.IsEnabled);

        moved.SetEnabled(true);
        Assert.Equal("\"C:\\New\\SuperBar\\current\\SuperBar.exe\"", RunValue());
    }

    [Fact]
    public void A_build_without_a_stable_path_cannot_register_but_can_always_unregister()
    {
        new RunKeyStartupRegistration(@"C:\SuperBar\current\SuperBar.exe", _runKey).SetEnabled(true);

        var development = new RunKeyStartupRegistration(executablePath: null, _runKey);
        Assert.False(development.IsAvailable);
        Assert.False(development.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => development.SetEnabled(true));

        // What the uninstall hook does.
        development.SetEnabled(false);
        Assert.Null(RunValue());

        // Removing what is not there is not an error.
        development.SetEnabled(false);
    }

    [Fact]
    public void Legacy_data_moves_to_the_data_folder_and_leaves_an_empty_install_folder_behind()
    {
        var legacy = Path.Combine(_root, "SuperBar");
        var data = Path.Combine(_root, "SuperBarData");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "settings.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "taskbar-recovery.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "logs", "superbar-1.log"), "log");

        Assert.True(LegacyDataMigration.TryMove(legacy, data));

        Assert.Equal("db", File.ReadAllText(Path.Combine(data, "settings.db")));
        Assert.True(File.Exists(Path.Combine(data, "taskbar-recovery.json")));
        Assert.True(File.Exists(Path.Combine(data, "logs", "superbar-1.log")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Legacy_data_never_replaces_existing_data_and_an_installation_is_left_alone()
    {
        var legacy = Path.Combine(_root, "SuperBar");
        var data = Path.Combine(_root, "SuperBarData");
        Directory.CreateDirectory(Path.Combine(legacy, "current"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(legacy, "Update.exe"), "updater");

        // An installation without data files: nothing to do.
        Assert.False(LegacyDataMigration.TryMove(legacy, data));

        // Both folders have a database: the newer data folder wins and the old file stays where it is.
        File.WriteAllText(Path.Combine(legacy, "settings.db"), "old");
        File.WriteAllText(Path.Combine(data, "settings.db"), "new");
        Assert.False(LegacyDataMigration.TryMove(legacy, data));
        Assert.Equal("new", File.ReadAllText(Path.Combine(data, "settings.db")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "settings.db")));

        // Data moves out of an installation, whose own files stay.
        File.Delete(Path.Combine(data, "settings.db"));
        Assert.True(LegacyDataMigration.TryMove(legacy, data));
        Assert.Equal("old", File.ReadAllText(Path.Combine(data, "settings.db")));
        Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
        Assert.True(Directory.Exists(Path.Combine(legacy, "current")));
    }
}
