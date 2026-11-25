using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Layout;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Services;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.ViewModels;
using SuperBar.Runtime.Views;
using SuperBar.Runtime.Widgets;
using SuperBar.Widgets;
using Windows.Graphics;

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
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _lifetime;
    private readonly ILogger<TaskbarRuntime> _logger;
    private readonly IUiTimer _displayDebounce;
    private TaskbarViewModel? _viewModel;
    private TaskbarWindow? _window;
    private WindowPreviewPopup? _preview;
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
        IUiDispatcher dispatcher,
        IAppLifetime lifetime,
        ILogger<TaskbarRuntime> logger)
    {
        _services = services;
        _settings = settings;
        _appBars = appBars;
        _nativeTaskbar = nativeTaskbar;
        _monitors = monitors;
        _tracker = tracker;
        _dispatcher = dispatcher;
        _lifetime = lifetime;
        _logger = logger;

        _displayDebounce = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(300), OnDisplaySettled);

        _settings.Changed += (_, e) => _dispatcher.TryEnqueue(() => OnSettingsChanged(e));
        shellEvents.ExplorerRestarted += (_, _) => _dispatcher.TryEnqueue(OnExplorerRestarted);
        shellEvents.DisplaySettingsChanged += (_, _) => _dispatcher.Invoke(() =>
        {
            _displayDebounce.Stop();
            _displayDebounce.Start();
        });
    }

    public bool IsEnabled { get; private set; }

    public event EventHandler? StateChanged;

    public void Enable()
    {
        _dispatcher.VerifyAccess();
        if (IsEnabled || _disposed)
        {
            return;
        }

        var settings = _settings.Current;
        _logger.LogInformation("Enabling SuperBar");

        _tracker.Start();
        if (_viewModel is null)
        {
            _viewModel = CreateViewModel();
            _viewModel.Apps.PinOrderChanged += keys =>
                _settings.Update(s => s.PinnedApps = AppOrdering.ReorderPins(s.PinnedApps, keys));
            _viewModel.Apps.PinRequested += (pin, index) => _settings.Update(s => AppOrdering.AddPin(s.PinnedApps, pin, index));
            _viewModel.Apps.UnpinRequested += key => _settings.Update(s => AppOrdering.RemovePin(s.PinnedApps, key));
        }

        _viewModel.Apply(settings);

        var monitor = _monitors.GetPrimaryMonitor();
        var chrome = _services.GetRequiredService<IWindowChrome>();
        var theme = _services.GetRequiredService<TaskbarTheme>();
        var windowLogger = _services.GetRequiredService<ILogger<TaskbarWindow>>();
        _preview = new WindowPreviewPopup(
            _services.GetRequiredService<IWindowThumbnailService>(),
            _services.GetRequiredService<ICursorService>(),
            chrome,
            _services.GetRequiredService<IWindowSwitcher>(),
            _tracker,
            _monitors,
            _dispatcher,
            theme,
            windowLogger);
        _window = new TaskbarWindow(_viewModel, theme, chrome, _preview, windowLogger);
        _window.ApplyAppearance(settings.Appearance);

        // Pre-place at the bottom edge (physical pixels) so there is no flash elsewhere; the AppBar takes over immediately.
        var height = (int)Math.Round(settings.Appearance.Height * monitor.Scale);
        _window.AppWindow.MoveAndResize(new RectInt32(monitor.Bounds.Left, monitor.Bounds.Bottom - height, monitor.Bounds.Width, height));

        // Like ShowActivated="False": appear without stealing focus from the user's current window.
        var hwnd = _window.Handle;
        chrome.ShowWithoutActivation(hwnd);
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
        _dispatcher.VerifyAccess();
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

        _preview?.Dispose();
        _preview = null;

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
        _lifetime.Shutdown();
    }

    private TaskbarViewModel CreateViewModel() => new(
        _services.GetRequiredService<IShellCommands>(),
        _tracker,
        _services.GetRequiredService<IWindowSwitcher>(),
        _services.GetRequiredService<IAppLauncher>(),
        _services.GetRequiredService<IconCache>(),
        _services.GetRequiredService<IWidgetRegistry>(),
        _services.GetRequiredService<WidgetViewResolver>(),
        _services.GetRequiredService<TaskbarTheme>(),
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
            _preview?.Hide(animate: false);
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

        if (_dispatcher.HasThreadAccess)
        {
            Disable();
        }
        else
        {
            _nativeTaskbar.Restore();
        }

        _viewModel?.Dispose();
        _displayDebounce.Dispose();
        _disposed = true;
    }
}
