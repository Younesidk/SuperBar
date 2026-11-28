using System.Reflection;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using Velopack;
using Velopack.Sources;

namespace SuperBar.App;

/// <summary>
/// The in-app updater: Velopack's <see cref="UpdateManager"/> reading the GitHub Releases of the SuperBar repository
/// (public, so no token; stable releases only).
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>The repository whose releases are the update feed. scripts\release.ps1 publishes to it.</summary>
    public const string RepositoryUrl = "https://github.com/Younesidk/SuperBar";

    private readonly IAppLifetime _lifetime;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly UpdateManager? _manager;
    private readonly Lock _gate = new();
    private UpdateState _state;
    private UpdateInfo? _available;
    private VelopackAsset? _downloaded;

    public VelopackUpdateService(IAppLifetime lifetime, ILogger<VelopackUpdateService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;

        try
        {
            // Does not touch the network. In a development build there is no installation: IsInstalled is false.
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The updater could not be created; updates are off for this session");
        }

        IsInstalled = _manager is { IsInstalled: true };
        CurrentVersion = (IsInstalled ? _manager!.CurrentVersion?.ToString() : null) ?? AssemblyVersion();

        // Downloaded in an earlier session and not applied at start-up (rare: Velopack applies it in Main).
        _downloaded = IsInstalled ? _manager!.UpdatePendingRestart : null;
        _state = !IsInstalled ? new UpdateState(UpdatePhase.NotInstalled)
            : _downloaded is not null ? new UpdateState(UpdatePhase.ReadyToRestart, _downloaded.Version.ToString())
            : new UpdateState(UpdatePhase.Idle);

        _logger.LogInformation("Updater: version {Version}, installed = {Installed}", CurrentVersion, IsInstalled);
    }

    public bool IsInstalled { get; }

    public string CurrentVersion { get; }

    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler? StateChanged;

    public async Task CheckForUpdatesAsync()
    {
        if (_manager is null || !TryEnter(UpdatePhase.Checking, s => s.Phase is UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.UpdateAvailable or UpdatePhase.Failed))
        {
            return;
        }

        try
        {
            // Task.Run: none of the updater's work may run on the UI thread the caller awaits from.
            var update = await Task.Run(() => _manager.CheckForUpdatesAsync()).ConfigureAwait(false);
            lock (_gate)
            {
                _available = update;
            }

            var version = update?.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update check: {Result}", version is null ? "up to date" : $"{version} is available");
            SetState(version is null ? new UpdateState(UpdatePhase.UpToDate) : new UpdateState(UpdatePhase.UpdateAvailable, version));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking for updates failed");
            SetState(new UpdateState(UpdatePhase.Failed, Error: ex.Message));
        }
    }

    public async Task DownloadUpdatesAsync(IProgress<int>? progress = null)
    {
        UpdateInfo? update;
        lock (_gate)
        {
            update = _available;
        }

        var version = update?.TargetFullRelease.Version.ToString();
        if (_manager is null || update is null
            || !TryEnter(UpdatePhase.Downloading, s => s.Phase is UpdatePhase.UpdateAvailable or UpdatePhase.Failed, version))
        {
            return;
        }

        try
        {
            await Task.Run(() => _manager.DownloadUpdatesAsync(update, percent =>
            {
                progress?.Report(percent);
                SetState(new UpdateState(UpdatePhase.Downloading, version, percent));
            })).ConfigureAwait(false);

            lock (_gate)
            {
                _downloaded = update.TargetFullRelease;
            }

            _logger.LogInformation("Update {Version} downloaded", version);
            SetState(new UpdateState(UpdatePhase.ReadyToRestart, version));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloading update {Version} failed", version);
            SetState(new UpdateState(UpdatePhase.Failed, version, Error: ex.Message));
        }
    }

    public void ApplyUpdateAndRestart()
    {
        VelopackAsset? update;
        lock (_gate)
        {
            update = _downloaded;
        }

        if (_manager is null || update is null)
        {
            return;
        }

        var version = update.Version.ToString();
        try
        {
            // Starts the updater, which waits (up to 60 s) for this process to exit before it touches any file.
            // The Control Center asked for the restart, so the new version opens it again.
            _logger.LogInformation("Restarting to apply update {Version}", version);
            _manager.WaitExitThenApplyUpdates(update, silent: false, restart: true, restartArgs: ["--control-center"]);
        }
        catch (Exception ex)
        {
            // Nothing was shut down: SuperBar keeps running as it was.
            _logger.LogError(ex, "The updater could not be started");
            SetState(new UpdateState(UpdatePhase.Failed, version, Error: ex.Message));
            return;
        }

        // The orderly exit: disposes the runtime, restores the native taskbar, ends the message loop. Should the
        // process be killed instead, the updater's BeforeUpdate hook restores the taskbar (see InstallerHooks).
        _lifetime.Shutdown();
    }

    /// <summary>Moves to <paramref name="phase"/> when the current state allows it; false when another operation is running.</summary>
    private bool TryEnter(UpdatePhase phase, Func<UpdateState, bool> allowed, string? version = null)
    {
        lock (_gate)
        {
            if (!allowed(_state))
            {
                return false;
            }

            _state = new UpdateState(phase, version);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SetState(UpdateState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The version the build was given (Directory.Build.props, or /p:Version of the release script).</summary>
    private static string AssemblyVersion()
    {
        var assembly = typeof(VelopackUpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // "0.1.0+<commit>": the build metadata is not part of the version users see.
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
