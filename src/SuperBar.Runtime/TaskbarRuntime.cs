using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Services;
using SuperBar.Runtime.ViewModels;
using SuperBar.Runtime.Views;
using SuperBar.Widgets;

namespace SuperBar.Runtime;

/// <summary>
/// Coordinates the taskbar runtime: the bar window, its AppBar registration, the native taskbar,
/// shell events (Explorer restart, display changes, full-screen apps) and live settings changes.
/// All work happens on the UI thread.
/// </summary>
public sealed class TaskbarRuntime : ITaskbarRuntimeController, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly IAppBarService _appBars;
    private readonly INativeTaskbarController _nativeTaskbar;
    private readonly IMonitorService _monitors;
    private readonly IWindowTracker _tracker;
    private readonly ILogger<TaskbarRuntime> _logger;
    private readonly DispatcherTimer _displayDebounce;
    private TaskbarViewModel? _viewModel;
    private TaskbarWindow? _window;
    private IAppBarRegistration? _appBar;
    private bool _disposed;

    public TaskbarRuntime(
        IServiceProvider services,
        ISettingsService settings,
        IAppBarService appBars,
        INativeTaskbarController nativeTaskbar,
        IMonitorService monitors,
        IWindowTracker tracker,
        IShellEvents shellEvents,
        ILogger<TaskbarRuntime> logger)
    {
        _services = services;
        _settings = settings;
        _appBars = appBars;
        _nativeTaskbar = nativeTaskbar;
        _monitors = monitors;
        _tracker = tracker;
        _logger = logger;

        _displayDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) => OnDisplaySettled());
        _displayDebounce.Stop();

        _settings.Changed += (_, e) => Dispatcher.UIThread.Post(() => OnSettingsChanged(e));
        shellEvents.ExplorerRestarted += (_, _) => Dispatcher.UIThread.Post(OnExplorerRestarted);
        shellEvents.DisplaySettingsChanged += (_, _) =>
        {
            _displayDebounce.Stop();
            _displayDebounce.Start();
        };
    }

    public bool IsEnabled { get; private set; }

    public event EventHandler? StateChanged;

    public void Enable()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsEnabled || _disposed)
        {
            return;
        }

        var settings = _settings.Current;
        _logger.LogInformation("Enabling SuperBar");

        _tracker.Start();
        _viewModel ??= CreateViewModel();
        _viewModel.Apply(settings);

        var monitor = _monitors.GetPrimaryMonitor();
        _window = new TaskbarWindow { DataContext = _viewModel };
        _window.ApplyAppearance(settings.Appearance);

        // Pre-place at the bottom edge so there is no flash elsewhere; the AppBar takes over immediately.
        _window.Position = new PixelPoint(monitor.Bounds.Left, monitor.Bounds.Bottom - (int)Math.Round(settings.Appearance.Height * monitor.Scale));
        _window.Width = monitor.Bounds.Width / monitor.Scale;
        _window.Height = settings.Appearance.Height;
        _window.Show();

        var hwnd = _window.Handle;
        _tracker.Ignore(hwnd);

        // Free the native taskbar's work area BEFORE negotiating our position so we get the real bottom edge.
        if (settings.Behavior.HideNativeTaskbar)
        {
            _nativeTaskbar.Hide();
        }

        _appBar = _appBars.Register(hwnd, CreateOptions(settings, monitor));
        _appBar.FullscreenAppChanged += OnFullscreenAppChanged;

        _viewModel.Activate();
        IsEnabled = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disable()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!IsEnabled)
        {
            return;
        }

        _logger.LogInformation("Disabling SuperBar");
        _viewModel?.Deactivate();

        if (_appBar is not null)
        {
            _appBar.FullscreenAppChanged -= OnFullscreenAppChanged;
            _appBar.Dispose();
            _appBar = null;
        }

        _nativeTaskbar.Restore();

        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
            _window = null;
        }

        IsEnabled = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Exit()
    {
        _logger.LogInformation("Exit requested");
        Disable();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private TaskbarViewModel CreateViewModel() => new(
        _services.GetRequiredService<IShellCommands>(),
        _tracker,
        _services.GetRequiredService<IWindowSwitcher>(),
        _services.GetRequiredService<IAppLauncher>(),
        _services.GetRequiredService<IconCache>(),
        _services.GetRequiredService<IWidgetRegistry>(),
        _services,
        _services.GetRequiredService<ILoggerFactory>(),
        openControlCenter: () => _services.GetRequiredService<IControlCenterLauncher>().Show(),
        exit: Exit);

    private static AppBarOptions CreateOptions(SuperBarSettings settings, MonitorInfo monitor) =>
        new(ScreenEdge.Bottom, settings.Appearance.Height, monitor.Handle);

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        if (!IsEnabled || _window is null || _viewModel is null)
        {
            return;
        }

        var current = e.Current;
        _viewModel.Apply(current);
        _window.ApplyAppearance(current.Appearance);

        if (Math.Abs(current.Appearance.Height - e.Previous.Appearance.Height) > 0.01)
        {
            _appBar?.Update(CreateOptions(current, _monitors.GetPrimaryMonitor()));
        }

        if (current.Behavior.HideNativeTaskbar != e.Previous.Behavior.HideNativeTaskbar)
        {
            if (current.Behavior.HideNativeTaskbar)
            {
                _nativeTaskbar.Hide();
            }
            else
            {
                _nativeTaskbar.Restore();
            }

            _appBar?.Reposition();
        }
    }

    private void OnExplorerRestarted()
    {
        if (!IsEnabled)
        {
            return;
        }

        // The shell forgets every AppBar when Explorer restarts.
        _logger.LogInformation("Re-registering AppBar after Explorer restart");
        _appBar?.Reregister();
    }

    private void OnDisplaySettled()
    {
        _displayDebounce.Stop();
        if (!IsEnabled || _appBar is null)
        {
            return;
        }

        // The primary monitor may have changed; re-target it.
        _appBar.Update(CreateOptions(_settings.Current, _monitors.GetPrimaryMonitor()));
    }

    private void OnFullscreenAppChanged(object? sender, bool fullscreen)
    {
        // Suspend widgets and timers while a game / video is full-screen.
        if (fullscreen)
        {
            _viewModel?.Deactivate();
        }
        else
        {
            _viewModel?.Activate();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Disable();
        }
        else
        {
            _nativeTaskbar.Restore();
        }

        _viewModel?.Dispose();
        _disposed = true;
    }
}
