namespace SuperBar.Core.Runtime;

/// <summary>Where the in-app updater stands.</summary>
public enum UpdatePhase
{
    /// <summary>A development build (run from the IDE or a plain build folder): there is nothing to update.</summary>
    NotInstalled,

    /// <summary>Installed; the update feed has not been asked yet in this session.</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>A newer version exists and has not been downloaded yet.</summary>
    UpdateAvailable,

    Downloading,

    /// <summary>The update is downloaded; it is applied when SuperBar restarts.</summary>
    ReadyToRestart,

    /// <summary>The last check or download failed. <see cref="UpdateState.AvailableVersion"/> tells which.</summary>
    Failed,
}

/// <summary>An immutable snapshot of the updater.</summary>
/// <param name="AvailableVersion">The newer version, once one is known (also kept when its download failed).</param>
/// <param name="DownloadProgress">0 to 100 while <see cref="UpdatePhase.Downloading"/>.</param>
/// <param name="Error">What went wrong, for <see cref="UpdatePhase.Failed"/>.</param>
public sealed record UpdateState(UpdatePhase Phase, string? AvailableVersion = null, int DownloadProgress = 0, string? Error = null);

/// <summary>
/// The in-app updater. It outlives the Control Center window, so a download keeps running (and its state is kept)
/// while the window is closed.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// True when SuperBar runs from an installation that can update itself. False for development builds; the
    /// operations below then do nothing.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>The running version, e.g. "0.1.0": the installed package's, or the assembly's in a development build.</summary>
    string CurrentVersion { get; }

    UpdateState State { get; }

    /// <summary>Raised after <see cref="State"/> changed. May be raised on any thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>Asks the update feed for a newer version. Never throws: a failure becomes <see cref="UpdatePhase.Failed"/>.</summary>
    Task CheckForUpdatesAsync();

    /// <summary>Downloads the update found by <see cref="CheckForUpdatesAsync"/>. Never throws.</summary>
    /// <param name="progress">Receives 0 to 100; the same value is published in <see cref="State"/>.</param>
    Task DownloadUpdatesAsync(IProgress<int>? progress = null);

    /// <summary>
    /// Applies the downloaded update: shuts SuperBar down in order (which restores the Windows taskbar), lets the
    /// updater swap the files, and starts the new version.
    /// </summary>
    void ApplyUpdateAndRestart();
}

/// <summary>Whether Windows starts SuperBar when the user signs in.</summary>
public interface IStartupRegistration
{
    /// <summary>
    /// False in a development build: its executable path is not a stable one, so it never registers itself (and
    /// never touches the registration of an installed SuperBar).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>True when Windows is set to start this installation at sign-in.</summary>
    bool IsEnabled { get; }

    /// <summary>Adds or removes the registration. Removing always works; adding requires <see cref="IsAvailable"/>.</summary>
    void SetEnabled(bool enabled);
}
