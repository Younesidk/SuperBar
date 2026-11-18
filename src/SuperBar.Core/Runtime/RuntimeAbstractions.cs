namespace SuperBar.Core.Runtime;

/// <summary>Controls the taskbar runtime. Used by the Control Center and the app lifecycle.</summary>
public interface ITaskbarRuntimeController
{
    bool IsEnabled { get; }

    event EventHandler? StateChanged;

    /// <summary>Shows the SuperBar, reserves screen space and (optionally) hides the native taskbar.</summary>
    void Enable();

    /// <summary>Removes the SuperBar and restores the native taskbar; the process keeps running.</summary>
    void Disable();

    /// <summary>Restores everything and exits the application.</summary>
    void Exit();
}

public interface IControlCenterLauncher
{
    void Show();
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
    string LogDirectory { get; }
    string RecoveryFilePath { get; }
}
