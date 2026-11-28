using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.Widgets;
using SuperBar.Widgets;

namespace SuperBar.Runtime.ViewModels;

/// <summary>A modular section of the single taskbar surface.</summary>
public abstract class SectionViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>Called when the bar becomes visible / un-suspended.</summary>
    public virtual void Activate()
    {
    }

    /// <summary>Called when the bar is hidden or a full-screen app suspends it; stop timers here.</summary>
    public virtual void Deactivate()
    {
    }
}

public sealed class StartSectionViewModel(string id, IShellCommands shell) : SectionViewModel(id)
{
    public ICommand ToggleStartCommand { get; } = new RelayCommand(shell.ToggleStartMenu);
}

/// <summary>
/// The taskbar slot of one hosted widget. It follows its <see cref="WidgetHost"/>: whenever the host's state
/// changes what the slot shows (first view, fault placeholder, restarted widget), the previous view is released
/// and <see cref="View"/> is re-evaluated.
/// </summary>
public sealed class WidgetSectionViewModel : SectionViewModel, IDisposable
{
    private readonly WidgetViewResolver _views;
    private readonly TaskbarTheme _theme;
    private double _width;
    private object? _source;
    private UIElement? _view;
    private ICollapsibleWidgetModel? _collapsible;
    private bool _disposed;

    public WidgetSectionViewModel(string id, WidgetHost host, WidgetViewResolver views, TaskbarTheme theme)
        : base(id)
    {
        Host = host;
        _views = views;
        _theme = theme;
        _width = SlotWidth();
        host.StateChanged += OnHostStateChanged;
    }

    public WidgetHost Host { get; }

    public string DisplayName => Host.Descriptor.DisplayName;

    /// <summary>
    /// The widget's UI element, created on first use. Follows the host: when the widget faults, the
    /// error placeholder replaces it; when it restarts, a fresh view does.
    /// </summary>
    public UIElement View
    {
        get
        {
            var source = Host.View;
            if (_view is null || !ReferenceEquals(source, _source))
            {
                ReleaseView();
                _source = source;
                _view = _views.Resolve(source);
                Track(source as ICollapsibleWidgetModel);
            }

            return _view;
        }
    }

    /// <summary>The widget's model collapsed its slot (see <see cref="ICollapsibleWidgetModel"/>); the bar then gives it no room at all.</summary>
    public bool IsCollapsed => _collapsible?.IsCollapsed ?? false;

    /// <summary>The element currently shown in the slot (what a flyout is anchored to), or null before the first render.</summary>
    public FrameworkElement? CurrentElement => _view as FrameworkElement;

    /// <summary>The slot width in DIPs, or NaN when the widget sizes itself to its content.</summary>
    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public double MinWidth => IsAutoSized ? Host.Descriptor.MinWidth : 0;

    public double MaxWidth => IsAutoSized ? Host.Descriptor.MaxWidth : double.PositiveInfinity;

    /// <summary>The shared widget card, or nothing for widgets that draw their own chrome.</summary>
    public Brush Background => Host.Descriptor.ShowBackground ? _theme.WidgetBackground : _theme.Transparent;

    private bool IsAutoSized => Host.Descriptor.Sizing == WidgetSizing.Auto;

    public void RefreshWidth() => Width = SlotWidth();

    public override void Activate() => Host.Activate();

    public override void Deactivate() => Host.Deactivate();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Host.StateChanged -= OnHostStateChanged;
        Host.Dispose();
        ReleaseView();
        Track(null);
    }

    private void Track(ICollapsibleWidgetModel? model)
    {
        if (ReferenceEquals(model, _collapsible))
        {
            return;
        }

        var was = IsCollapsed;
        if (_collapsible is not null)
        {
            _collapsible.PropertyChanged -= OnModelChanged;
        }

        _collapsible = model;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
        }

        if (was != IsCollapsed)
        {
            OnPropertyChanged(nameof(IsCollapsed));
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ICollapsibleWidgetModel.IsCollapsed) or null or "")
        {
            OnPropertyChanged(nameof(IsCollapsed));
        }
    }

    private double SlotWidth() => IsAutoSized ? double.NaN : Host.Width;

    private void OnHostStateChanged(object? sender, EventArgs e)
    {
        // Only once a view has been handed out: before that the slot's first binding picks up the current one.
        if (!_disposed && _source is not null && !ReferenceEquals(Host.View, _source))
        {
            OnPropertyChanged(nameof(View));
        }
    }

    private void ReleaseView()
    {
        // A view created for a model is ours to dispose; a UI element supplied by the widget itself is not.
        if (_view is IDisposable disposable && !ReferenceEquals(_view, _source))
        {
            disposable.Dispose();
        }

        _view = null;
        _source = null;
    }
}

/// <summary>
/// System section. Real notification-area (tray icon) hosting requires SuperBar to own the shell's
/// Shell_TrayWnd notification protocol and is NOT implemented in this milestone; until then the overflow button
/// opens Explorer's own "hidden icons" flyout. The quick-settings button opens the real Windows Quick Settings flyout
/// and shows the live network, volume and battery icons (see <see cref="TrayIndicators"/> and <c>BatteryIcon</c>).
/// <para>
/// The network and battery services are followed only while the section is active (the bar is shown and not suspended
/// by a full-screen app) AND its view is loaded: subscribing makes the services attach their Windows events and
/// leaving makes them detach, so a hidden bar or an unloaded tray costs nothing and leaks nothing.
/// </para>
/// </summary>
public sealed class SystemTraySectionViewModel : SectionViewModel
{
    private readonly INetworkStateService _network;
    private readonly IBatteryStateService _battery;
    private readonly TaskbarTheme _theme;
    private NetworkVisual _networkVisual = TrayIndicators.For(NetworkState.Unknown);
    private BatteryState _batteryState = BatteryState.NotPresent;
    private bool _active;
    private bool _viewLoaded;
    private bool _listening;

    public SystemTraySectionViewModel(string id, IShellCommands shell, INetworkStateService network, IBatteryStateService battery, TaskbarTheme theme)
        : base(id)
    {
        _network = network;
        _battery = battery;
        _theme = theme;
        ToggleQuickSettingsCommand = new RelayCommand(shell.ToggleQuickSettings);
        ToggleOverflowCommand = new RelayCommand(shell.ToggleTrayOverflow);
    }

    public ICommand ToggleQuickSettingsCommand { get; }

    public ICommand ToggleOverflowCommand { get; }

    public string NetworkGlyph => _networkVisual.Glyph;

    /// <summary>The empty signal bars behind a partial Wi-Fi / cellular glyph; empty when there are none.</summary>
    public string NetworkGhostGlyph => _networkVisual.Ghost;

    public Brush NetworkBrush => _networkVisual.IsDimmed ? _theme.Subtle : _theme.Foreground;

    public string NetworkToolTip => _networkVisual.ToolTip;

    /// <summary>Only on machines with a system battery; desktops never show the icon.</summary>
    public bool HasBattery => _batteryState.IsPresent;

    public double BatteryPercent => _batteryState.Percent;

    public bool BatteryIsCharging => _batteryState.IsCharging;

    public bool BatteryIsPluggedIn => _batteryState.IsPluggedIn;

    public bool BatteryIsEnergySaverOn => _batteryState.IsSaverOn;

    public string BatteryToolTip { get; private set; } = "";

    public override void Activate()
    {
        _active = true;
        UpdateListening();
    }

    public override void Deactivate()
    {
        _active = false;
        UpdateListening();
    }

    /// <summary>The tray's view entered (true) or left (false) the visual tree.</summary>
    public void SetViewLoaded(bool loaded)
    {
        _viewLoaded = loaded;
        UpdateListening();
    }

    private void UpdateListening()
    {
        var wanted = _active && _viewLoaded;
        if (wanted == _listening)
        {
            return;
        }

        _listening = wanted;
        if (wanted)
        {
            _network.Changed += OnNetworkChanged;
            _battery.Changed += OnBatteryChanged;

            // The services keep their last state, so the icons are right at once and refresh as soon as Windows answers.
            ApplyNetwork(_network.Current);
            ApplyBattery(_battery.Current);
        }
        else
        {
            _network.Changed -= OnNetworkChanged;
            _battery.Changed -= OnBatteryChanged;
        }
    }

    private void OnNetworkChanged(object? sender, NetworkState state) => ApplyNetwork(state);

    private void OnBatteryChanged(object? sender, BatteryState state) => ApplyBattery(state);

    private void ApplyNetwork(NetworkState state)
    {
        var visual = TrayIndicators.For(state);
        if (visual == _networkVisual)
        {
            return;
        }

        _networkVisual = visual;
        OnPropertyChanged(nameof(NetworkGlyph));
        OnPropertyChanged(nameof(NetworkGhostGlyph));
        OnPropertyChanged(nameof(NetworkBrush));
        OnPropertyChanged(nameof(NetworkToolTip));
    }

    private void ApplyBattery(BatteryState state)
    {
        if (state == _batteryState && BatteryToolTip.Length > 0)
        {
            return;
        }

        _batteryState = state;
        BatteryToolTip = state.IsPresent ? TrayIndicators.BatteryToolTip(state) : "";
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryPercent));
        OnPropertyChanged(nameof(BatteryIsCharging));
        OnPropertyChanged(nameof(BatteryIsPluggedIn));
        OnPropertyChanged(nameof(BatteryIsEnergySaverOn));
        OnPropertyChanged(nameof(BatteryToolTip));
    }
}
