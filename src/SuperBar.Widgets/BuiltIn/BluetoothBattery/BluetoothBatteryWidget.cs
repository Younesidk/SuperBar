using SuperBar.Core.Mvvm;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Battery levels of the connected Bluetooth devices that report one: an icon and a percentage per device on the
/// bar, and every device with a battery bar in its flyout. Windows is asked again every
/// <see cref="BluetoothBatterySettings.RefreshSeconds"/> on the thread pool, and right away when the flyout opens.
/// <para>
/// One or two devices are shown inline. With more (or in compact mode) the bar shows the lowest one and "+N".
/// A level at or below <see cref="BluetoothBatterySettings.LowBatteryThreshold"/> is shown as low (amber), and at
/// or below half of it as critical (red).
/// </para>
/// </summary>
[Widget("superbar.bluetooth-battery", "Bluetooth Battery",
    Description = "Battery levels of your connected Bluetooth headphones, mice, keyboards and controllers.",
    Glyph = "",
    DefaultWidth = 72,
    MinWidth = 36,
    MaxWidth = 220,
    Sizing = WidgetSizing.Auto,
    AllowMultiple = false)]
public sealed class BluetoothBatteryWidget : WidgetBase<BluetoothBatterySettings, BluetoothBatteryModel>
{
    /// <summary>The most devices shown inline before the bar switches to "lowest + N".</summary>
    public const int MaxInline = 2;

    /// <summary>How long one enumeration may take before it is abandoned (and retried with back-off).</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    private readonly IBluetoothBatteryService _service;
    private IWidgetScope? _scope;
    private IDisposable? _poll;
    private IReadOnlyList<BluetoothBatteryDevice> _devices = [];

    public BluetoothBatteryWidget(WidgetContext context, IBluetoothBatteryService service)
        : base(context, new BluetoothBatteryModel())
    {
        _service = service;
        Flyout = new BluetoothBatteryFlyoutModel();
        On(Model.Open, ToggleFlyout);
        On(Flyout.Refresh, RefreshNow);
        AddMenuItem("Compact", () => SaveSettings(s => s.Compact = !s.Compact), "", () => Settings.Compact);
        AddMenuItem("Refresh", RefreshNow, "");
    }

    /// <summary>The flyout's content; one instance for the widget's lifetime, so toggling it closes it again.</summary>
    public BluetoothBatteryFlyoutModel Flyout { get; }

    protected override void OnActivated(IWidgetScope scope)
    {
        _scope = scope;
        Show();
        StartPolling();
    }

    protected override void OnDeactivated()
    {
        // The scope already stopped the poll and dropped pending results.
        _scope = null;
        _poll = null;
        Flyout.SetRefreshing(false);
    }

    protected override void OnSettingsChanged(BluetoothBatterySettings previous)
    {
        if (previous.RefreshSeconds != Settings.RefreshSeconds)
        {
            StartPolling();
        }

        Show();
    }

    /// <summary>The poll interval: the setting, kept inside its declared range even if the stored value is not.</summary>
    internal TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(Settings.RefreshSeconds, 10, 300));

    private void StartPolling()
    {
        _poll?.Dispose();
        _poll = null;
        if (_scope is { } scope)
        {
            _poll = scope.Poll(RefreshInterval, _service.GetConnectedDevicesAsync, Apply, QueryTimeout, runImmediately: true);
        }
    }

    private void ToggleFlyout()
    {
        Context.Flyout.Toggle(Flyout);
        if (Context.Flyout.IsOpen)
        {
            RefreshNow();
        }
    }

    private void RefreshNow()
    {
        if (_scope is not { } scope || Flyout.IsRefreshing)
        {
            return;
        }

        Flyout.SetRefreshing(true);
        scope.Run(
            _service.GetConnectedDevicesAsync,
            devices =>
            {
                Flyout.SetRefreshing(false);
                Apply(devices);
            },
            QueryTimeout,
            _ => Flyout.SetRefreshing(false));
    }

    private void Apply(IReadOnlyList<BluetoothBatteryDevice> devices)
    {
        _devices = devices;
        Show();
    }

    private void Show()
    {
        var items = BluetoothBatteryItem.From(_devices, Settings.LowBatteryThreshold);
        Model.Show(items, Settings.Compact, Settings.HideWhenEmpty);
        Flyout.Show(items);
    }
}

public sealed class BluetoothBatterySettings
{
    [Setting("Hide when no devices connected", Description = "Takes no room on the bar while no connected device reports its battery.")]
    public bool HideWhenEmpty { get; set; } = true;

    [Setting("Low battery warning threshold", Description = "Levels at or below this turn amber; at or below half of it, red.", Minimum = 5, Maximum = 40, Step = 5, Unit = "%")]
    public int LowBatteryThreshold { get; set; } = 30;

    [Setting("Refresh interval", Description = "How often Windows is asked for new levels.", Minimum = 10, Maximum = 300, Step = 10, Unit = "s")]
    public int RefreshSeconds { get; set; } = 30;

    [Setting("Compact", Description = "Always show only the lowest device and how many others are connected.")]
    public bool Compact { get; set; }
}

/// <summary>How worried the bar should look about a battery level.</summary>
public enum BatteryLevel
{
    Normal,
    Low,
    Critical,
}

/// <summary>One device as the bar and the flyout show it. Immutable, with value equality, so unchanged polls cost nothing.</summary>
public sealed record BluetoothBatteryItem(string Id, string Name, int Percent, BluetoothDeviceKind Kind, BatteryLevel Level)
{
    /// <summary>Segoe Fluent Icons glyph for <see cref="Kind"/>.</summary>
    public string Glyph => GlyphFor(Kind);

    public string PercentText => $"{Percent}%";

    /// <summary>0..1, for battery bars.</summary>
    public double Fraction => Percent / 100.0;

    public static string GlyphFor(BluetoothDeviceKind kind) => kind switch
    {
        BluetoothDeviceKind.Headphones or BluetoothDeviceKind.Earbuds => "", // Headphone
        BluetoothDeviceKind.Mouse => "", // Mouse
        BluetoothDeviceKind.Keyboard => "", // KeyboardClassic
        BluetoothDeviceKind.Gamepad => "", // Game
        BluetoothDeviceKind.Speaker => "", // Speakers
        _ => "", // Bluetooth
    };

    /// <summary>Low at or below <paramref name="lowThreshold"/>, critical at or below half of it.</summary>
    public static BatteryLevel LevelOf(int percent, int lowThreshold) =>
        percent <= lowThreshold / 2 ? BatteryLevel.Critical
        : percent <= lowThreshold ? BatteryLevel.Low
        : BatteryLevel.Normal;

    /// <summary>The devices lowest level first (then by name), whatever order the service used.</summary>
    internal static IReadOnlyList<BluetoothBatteryItem> From(IEnumerable<BluetoothBatteryDevice> devices, int lowThreshold) =>
        devices
            .Where(d => d.BatteryPercent is >= 0 and <= 100)
            .OrderBy(d => d.BatteryPercent)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => new BluetoothBatteryItem(d.Id, d.Name, d.BatteryPercent, d.Kind, LevelOf(d.BatteryPercent, lowThreshold)))
            .ToList();
}

/// <summary>Presentation state of <see cref="BluetoothBatteryWidget"/> on the bar.</summary>
public sealed class BluetoothBatteryModel : ObservableObject, ICollapsibleWidgetModel
{
    private IReadOnlyList<BluetoothBatteryItem> _items = [];
    private int _hiddenCount;
    private bool _hasDevices;
    private bool _isCollapsed;
    private string _toolTip = "No Bluetooth devices with battery info";

    /// <summary>Raised by the view when the pill is clicked: toggles the flyout.</summary>
    public WidgetCommand Open { get; } = new();

    /// <summary>The devices drawn on the bar: all of them (one or two), or only the lowest one.</summary>
    public IReadOnlyList<BluetoothBatteryItem> Items { get => _items; private set => SetProperty(ref _items, value); }

    /// <summary>Connected devices not drawn on the bar; shown as "+N" when above zero.</summary>
    public int HiddenCount
    {
        get => _hiddenCount;
        private set
        {
            if (SetProperty(ref _hiddenCount, value))
            {
                OnPropertyChanged(nameof(HiddenText));
            }
        }
    }

    public string HiddenText => _hiddenCount > 0 ? $"+{_hiddenCount}" : "";

    public bool HasDevices { get => _hasDevices; private set => SetProperty(ref _hasDevices, value); }

    public bool IsCollapsed { get => _isCollapsed; private set => SetProperty(ref _isCollapsed, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    internal void Show(IReadOnlyList<BluetoothBatteryItem> all, bool compact, bool hideWhenEmpty)
    {
        var inline = !compact && all.Count <= BluetoothBatteryWidget.MaxInline ? all : all.Take(1).ToList();
        if (!inline.SequenceEqual(Items))
        {
            Items = inline;
        }

        HiddenCount = all.Count - inline.Count;
        HasDevices = all.Count > 0;
        ToolTip = all.Count > 0
            ? string.Join("\n", all.Select(d => $"{d.Name}: {d.PercentText}"))
            : "No Bluetooth devices with battery info";
        IsCollapsed = hideWhenEmpty && all.Count == 0;
    }
}

/// <summary>Content of the widget's flyout: every device, with a refresh button.</summary>
public sealed class BluetoothBatteryFlyoutModel : ObservableObject
{
    private IReadOnlyList<BluetoothBatteryItem> _devices = [];
    private bool _isRefreshing;

    /// <summary>Asks Windows for new levels now.</summary>
    public WidgetCommand Refresh { get; } = new();

    public IReadOnlyList<BluetoothBatteryItem> Devices { get => _devices; private set => SetProperty(ref _devices, value); }

    public bool IsRefreshing { get => _isRefreshing; private set => SetProperty(ref _isRefreshing, value); }

    internal void Show(IReadOnlyList<BluetoothBatteryItem> devices)
    {
        if (!devices.SequenceEqual(Devices))
        {
            Devices = devices;
        }
    }

    internal void SetRefreshing(bool refreshing)
    {
        IsRefreshing = refreshing;
        Refresh.IsEnabled = !refreshing;
    }
}
