using Microsoft.Extensions.Logging;
using SuperBar.Core.Layout;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Runtime.Services;

/// <summary>
/// Keeps the bar's pins in line with the Windows taskbar's while <see cref="BehaviorSettings.SyncWindowsPins"/> is on:
/// at every start (sign-in included), whenever pins change in Windows, and after Explorer restarts.
/// <para>
/// The synced pins are saved with the settings and their icons in <see cref="IPinIconStore"/>, so a start during
/// sign-in shows the last known pins at once; the sync that follows retries with a backoff while Explorer is still
/// coming up (the Taskband value not written yet, packaged apps not registered yet).
/// </para>
/// The merge rules live in <see cref="WindowsPinMerge"/>. Started on the UI thread.
/// </summary>
public sealed class WindowsPinSync : IDisposable
{
    /// <summary>Start-up and Explorer restarts: Explorer may need a while.</summary>
    private static readonly TimeSpan[] SlowRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    /// <summary>A change while running: Explorer is up, a short second chance is enough.</summary>
    private static readonly TimeSpan[] QuickRetries = [TimeSpan.FromSeconds(2)];

    private readonly IWindowsTaskbarPinService _windowsPins;
    private readonly ISettingsService _settings;
    private readonly IPinIconStore _iconStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WindowsPinSync> _logger;
    private readonly Lock _gate = new(); // continuations after an await may run on the thread pool
    private CancellationTokenSource _lifetime = new();
    private bool _started;
    private bool _syncing;
    private (string Reason, TimeSpan[] Retries)? _pending;
    private bool _disposed;

    public WindowsPinSync(
        IWindowsTaskbarPinService windowsPins,
        ISettingsService settings,
        IPinIconStore iconStore,
        IUiDispatcher dispatcher,
        ILogger<WindowsPinSync> logger)
    {
        _windowsPins = windowsPins;
        _settings = settings;
        _iconStore = iconStore;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>A sync saved new icons for the pins. Raised on the UI thread.</summary>
    public event EventHandler? IconsChanged;

    private bool Enabled => _settings.Current.Behavior.SyncWindowsPins;

    public void Start()
    {
        _dispatcher.VerifyAccess();
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        _windowsPins.PinsChanged += OnWindowsPinsChanged;
        if (Enabled)
        {
            _windowsPins.StartWatching();
            Request("start-up", SlowRetries);
        }
        else
        {
            _logger.LogInformation("Windows taskbar pin sync is off");
        }
    }

    /// <summary>Explorer restarted: it may have rewritten its pins.</summary>
    public void OnExplorerRestarted() => _dispatcher.Invoke(() => Request("Explorer restarted", SlowRetries));

    private void OnWindowsPinsChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() => Request("pins changed in Windows", QuickRetries));

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Current.Behavior.SyncWindowsPins == e.Previous.Behavior.SyncWindowsPins)
        {
            // "Reset all settings" drops the synced pins: bring Windows' back right away.
            if (e.Current.Behavior.SyncWindowsPins
                && e.Previous.PinnedApps.Any(p => p.PinnedInWindows) && !e.Current.PinnedApps.Any(p => p.PinnedInWindows))
            {
                _dispatcher.TryEnqueue(() => Request("settings reset", QuickRetries));
            }

            return;
        }

        _dispatcher.Invoke(() =>
        {
            if (e.Current.Behavior.SyncWindowsPins)
            {
                _logger.LogInformation("Windows taskbar pin sync turned on");
                _windowsPins.StartWatching();
                Request("sync turned on", SlowRetries);
            }
            else
            {
                // Pins stay as they are, flagged; the bar just stops showing the ones only Windows pins.
                _logger.LogInformation("Windows taskbar pin sync turned off");
                _windowsPins.StopWatching();
                lock (_gate)
                {
                    _lifetime.Cancel();
                    _lifetime.Dispose();
                    _lifetime = new CancellationTokenSource();
                    _pending = null;
                }
            }
        });
    }

    /// <summary>One sync at a time; a request during a sync runs once more right after it.</summary>
    private void Request(string reason, TimeSpan[] retries)
    {
        if (_disposed || !Enabled)
        {
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            if (_syncing)
            {
                _pending = (reason, retries);
                return;
            }

            _syncing = true;
            token = _lifetime.Token;
        }

        _ = RunAsync(reason, retries, token);
    }

    private async Task RunAsync(string reason, TimeSpan[] retries, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await SyncAsync(reason, retries, token);
                lock (_gate)
                {
                    if (_pending is not { } next || token.IsCancellationRequested)
                    {
                        _syncing = false;
                        return;
                    }

                    _pending = null;
                    (reason, retries) = next;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Sync turned off or SuperBar exiting.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Windows taskbar pin sync failed ({Reason})", reason);
        }
        lock (_gate)
        {
            _syncing = false;
        }
    }

    private async Task SyncAsync(string reason, TimeSpan[] retries, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var last = attempt >= retries.Length;
            var result = await _windowsPins.ReadAsync(IconCache.PinIconPixelSize, token);
            token.ThrowIfCancellationRequested();

            if (result is null)
            {
                if (last)
                {
                    _logger.LogInformation("Windows taskbar pins still not readable ({Reason}); keeping the last synced pins", reason);
                    return;
                }
            }
            else
            {
                // A partial read only adds and updates. Once retries run out it is all Windows has: take it as final.
                var complete = result.IsComplete || last;
                await ApplyAsync(result, complete, reason, token);
                if (complete)
                {
                    return;
                }
            }

            var delay = retries[attempt];
            _logger.LogInformation("Windows taskbar pins not fully readable yet ({Reason}); retrying in {Delay:0.#} s", reason, delay.TotalSeconds);
            await Task.Delay(delay, token);
        }
    }

    private async Task ApplyAsync(WindowsPinReadResult result, bool complete, string reason, CancellationToken token)
    {
        var windowsPins = result.Pins.Select(ToPinnedApp).ToList();

        // Icons first (file I/O off the UI thread), so the bar never shows a new pin with a stale icon.
        var iconsChanged = await Task.Run(() => SaveIcons(result.Pins, windowsPins), token);
        token.ThrowIfCancellationRequested();

        // Dry run on a copy: a sync that changes nothing must not rewrite the settings (Explorer touches its pins often).
        var probe = _settings.Current.PinnedApps.Select(p => p.Clone()).ToList();
        var preview = WindowsPinMerge.Apply(probe, windowsPins, complete);
        if (preview.Changed)
        {
            WindowsPinMergeResult? applied = null;
            _settings.Update(s => applied = WindowsPinMerge.Apply(s.PinnedApps, windowsPins.Select(p => p.Clone()).ToList(), complete));
            _logger.LogInformation(
                "Windows taskbar pin sync ({Reason}): {Added} added, {Removed} removed, {Updated} updated{Reordered}; {Count} pins on the bar",
                reason, applied!.Added, applied.Removed, applied.Updated, applied.Reordered ? ", reordered" : "",
                AppOrdering.ShownPins(_settings.Current).Count);
        }
        else
        {
            _logger.LogDebug("Windows taskbar pin sync ({Reason}): already up to date", reason);
        }

        if (iconsChanged > 0)
        {
            _logger.LogDebug("Windows taskbar pin sync: {Count} icon(s) updated", iconsChanged);
            _dispatcher.Invoke(() => IconsChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private int SaveIcons(IReadOnlyList<WindowsTaskbarPin> pins, IReadOnlyList<PinnedApp> asPins)
    {
        var changed = 0;
        for (var i = 0; i < pins.Count; i++)
        {
            if (pins[i].Icon is not { } icon)
            {
                continue;
            }

            try
            {
                if (_iconStore.Save(AppOrdering.PinKey(asPins[i]), icon))
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not cache the icon of {Pin}", pins[i].DisplayName);
            }
        }

        return changed;
    }

    private static PinnedApp ToPinnedApp(WindowsTaskbarPin pin) => new()
    {
        DisplayName = pin.DisplayName,
        Path = pin.Path,
        Arguments = pin.Arguments,
        WorkingDirectory = pin.WorkingDirectory,
        ShortcutPath = pin.ShortcutPath,
        AppUserModelId = pin.AppUserModelId,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _settings.Changed -= OnSettingsChanged;
            _windowsPins.PinsChanged -= OnWindowsPinsChanged;
        }

        _windowsPins.StopWatching();
        lock (_gate)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
