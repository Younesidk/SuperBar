using Microsoft.Win32;
using SuperBar.Core.Runtime;

namespace SuperBar.Windows.Startup;

/// <summary>
/// "Start with Windows" through the per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run): no
/// elevation, no scheduled task, and the entry shows up under Startup apps in Windows Settings and Task Manager.
///
/// The value holds the quoted path of the installed executable. Velopack keeps that path stable: it installs to
/// %LocalAppData%\SuperBar\current\SuperBar.exe and an update replaces the contents of <c>current</c>, never its name.
/// </summary>
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "SuperBar";

    private readonly string? _executablePath;
    private readonly string _runKeyPath;
    private readonly string _valueName;

    /// <param name="executablePath">The installed executable, or null when this build must not register itself.</param>
    /// <param name="runKeyPath">The key under HKEY_CURRENT_USER; only tests pass another one.</param>
    public RunKeyStartupRegistration(string? executablePath, string runKeyPath = DefaultRunKeyPath, string valueName = DefaultValueName)
    {
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
        _runKeyPath = runKeyPath;
        _valueName = valueName;
    }

    public bool IsAvailable => _executablePath is not null;

    /// <summary>The command line Windows runs at sign-in, or null when this build cannot register itself.</summary>
    public string? Command => _executablePath is null ? null : $"\"{_executablePath}\"";

    /// <summary>True when the Run value exists and points at this executable.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return Command is not null && string.Equals(key?.GetValue(_valueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        if (Command is null)
        {
            throw new InvalidOperationException("This build of SuperBar cannot register itself to start with Windows.");
        }

        // Also rewrites a value that points somewhere else (SuperBar was installed to another folder before).
        if (!IsEnabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(_valueName, Command, RegistryValueKind.String);
        }
    }
}
