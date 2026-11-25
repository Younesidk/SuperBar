using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter.ViewModels;

public sealed class ControlCenterViewModel : ObservableObject, IDisposable
{
    private PageViewModel _selectedPage;

    public ControlCenterViewModel(ISettingsService settings, ITaskbarRuntimeController runtime, IWidgetRegistry widgets, IAppPaths paths, IAppLauncher launcher, IUiDispatcher dispatcher)
    {
        Pages = (PageViewModel[])
        [
            new GeneralPageViewModel(settings, runtime),
            new AppearancePageViewModel(settings, dispatcher),
            new LayoutPageViewModel(settings, widgets),
            new WidgetsPageViewModel(settings, widgets, dispatcher),
            new ApplicationsPageViewModel(settings),
            new AboutPageViewModel(paths, launcher),
        ];
        _selectedPage = Pages[0];
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && SetProperty(ref _selectedPage, value))
            {
                value.OnNavigatedTo();
            }
        }
    }

    /// <summary>Detaches the pages from long-lived services when the window closes.</summary>
    public void Dispose()
    {
        foreach (var page in Pages.OfType<IDisposable>())
        {
            page.Dispose();
        }
    }
}

public abstract class PageViewModel(string title, string iconGlyph, string subtitle) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code point shown in the navigation pane.</summary>
    public string IconGlyph { get; } = iconGlyph;

    public string Subtitle { get; } = subtitle;

    public virtual void OnNavigatedTo()
    {
    }
}

/// <summary>Runs the most recently scheduled action once no new one has been scheduled for the delay (slider drags).</summary>
internal sealed class Debouncer
{
    private readonly IUiTimer _timer;
    private Action? _pending;

    public Debouncer(IUiDispatcher dispatcher, TimeSpan delay)
    {
        _timer = dispatcher.CreateTimer(delay, () =>
        {
            _timer!.Stop();
            var action = _pending;
            _pending = null;
            action?.Invoke();
        });
    }

    public void Run(Action action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }
}

// ------------------------------------------------------------------------------------------------

public sealed class GeneralPageViewModel : PageViewModel, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ITaskbarRuntimeController _runtime;
    private bool _hideNativeTaskbar;

    public GeneralPageViewModel(ISettingsService settings, ITaskbarRuntimeController runtime)
        : base("General", "\uE713", "Turn SuperBar on or off and control how it replaces the Windows taskbar.")
    {
        _settings = settings;
        _runtime = runtime;
        _hideNativeTaskbar = settings.Current.Behavior.HideNativeTaskbar;
        runtime.StateChanged += OnRuntimeStateChanged;
        ExitCommand = new RelayCommand(runtime.Exit);
        ResetCommand = new RelayCommand(() =>
        {
            _settings.ResetToDefaults();
            HideNativeTaskbar = _settings.Current.Behavior.HideNativeTaskbar;
        });
    }

    public bool IsSuperBarEnabled
    {
        get => _runtime.IsEnabled;
        set
        {
            if (value == _runtime.IsEnabled)
            {
                return;
            }

            if (value)
            {
                _runtime.Enable();
            }
            else
            {
                _runtime.Disable();
            }

            OnPropertyChanged();
        }
    }

    public bool HideNativeTaskbar
    {
        get => _hideNativeTaskbar;
        set
        {
            if (SetProperty(ref _hideNativeTaskbar, value))
            {
                _settings.Update(s => s.Behavior.HideNativeTaskbar = value);
            }
        }
    }

    public ICommand ExitCommand { get; }

    public ICommand ResetCommand { get; }

    private void OnRuntimeStateChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IsSuperBarEnabled));

    public void Dispose() => _runtime.StateChanged -= OnRuntimeStateChanged;
}

public sealed class AppearancePageViewModel : PageViewModel
{
    private static readonly string[] SwatchColors = ["#5B8CFF", "#8B6CFF", "#2FBF9B", "#F2A541", "#F2545B", "#E8E8EC"];

    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce;
    private Action<SuperBarSettings>? _pendingChanges;
    private double _height;
    private double _opacity;
    private double _cornerRadius;
    private double _spacing;
    private bool _darkTheme;
    private bool _useAcrylic;
    private string _accent;

    public AppearancePageViewModel(ISettingsService settings, IUiDispatcher dispatcher)
        : base("Appearance", "", "Size, transparency, corners and colors of the bar.")
    {
        _settings = settings;
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(120));
        var a = settings.Current.Appearance;
        _height = a.Height;
        _opacity = a.BackgroundOpacity;
        _cornerRadius = a.ItemCornerRadius;
        _spacing = a.ItemSpacing;
        _darkTheme = a.Theme == ThemeMode.Dark;
        _useAcrylic = a.UseAcrylic;
        _accent = a.AccentColor;
        SetAccentCommand = new RelayCommand(p =>
        {
            if (p is string color)
            {
                Accent = color;
            }
        });
        AccentSwatches = SwatchColors.Select(hex => new AccentSwatchViewModel(hex, SetAccentCommand)).ToList();
    }

    public double MinHeight => AppearanceSettings.MinHeight;
    public double MaxHeight => AppearanceSettings.MaxHeight;

    public IReadOnlyList<AccentSwatchViewModel> AccentSwatches { get; }

    public ICommand SetAccentCommand { get; }

    public double TaskbarHeight
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, Math.Round(value)))
            {
                OnPropertyChanged(nameof(TaskbarHeightLabel));
                Push(s => s.Appearance.Height = _height);
            }
        }
    }

    public string TaskbarHeightLabel => FormatPx(_height);

    public double BackgroundOpacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, value))
            {
                OnPropertyChanged(nameof(BackgroundOpacityLabel));
                Push(s => s.Appearance.BackgroundOpacity = _opacity);
            }
        }
    }

    public string BackgroundOpacityLabel => _opacity.ToString("P0", CultureInfo.CurrentCulture);

    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            if (SetProperty(ref _cornerRadius, Math.Round(value)))
            {
                OnPropertyChanged(nameof(CornerRadiusLabel));
                Push(s => s.Appearance.ItemCornerRadius = _cornerRadius);
            }
        }
    }

    public string CornerRadiusLabel => FormatPx(_cornerRadius);

    public double ItemSpacing
    {
        get => _spacing;
        set
        {
            if (SetProperty(ref _spacing, Math.Round(value)))
            {
                OnPropertyChanged(nameof(ItemSpacingLabel));
                Push(s => s.Appearance.ItemSpacing = _spacing);
            }
        }
    }

    public string ItemSpacingLabel => FormatPx(_spacing);

    public bool DarkTheme { get => _darkTheme; set { if (SetProperty(ref _darkTheme, value)) Push(s => s.Appearance.Theme = value ? ThemeMode.Dark : ThemeMode.Light); } }

    public bool UseAcrylic { get => _useAcrylic; set { if (SetProperty(ref _useAcrylic, value)) Push(s => s.Appearance.UseAcrylic = value); } }

    public string Accent { get => _accent; set { if (SetProperty(ref _accent, value)) Push(s => s.Appearance.AccentColor = value); } }

    internal static string FormatPx(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0} px");

    /// <summary>
    /// Queues a change and writes all queued changes together once edits pause, so a slider drag is one write and
    /// quick successive edits of different settings (e.g. theme then accent) are combined, never dropped.
    /// </summary>
    private void Push(Action<SuperBarSettings> change)
    {
        _pendingChanges += change;
        _debounce.Run(() =>
        {
            var changes = _pendingChanges;
            _pendingChanges = null;
            if (changes is not null)
            {
                _settings.Update(changes);
            }
        });
    }
}

/// <summary>One accent color button: its own brush and command, so the template needs no ancestor binding.</summary>
public sealed class AccentSwatchViewModel
{
    public AccentSwatchViewModel(string hex, ICommand selectCommand)
    {
        Hex = hex;
        SelectCommand = selectCommand;
        var c = ColorHex.ParseOrDefault(hex, new ArgbColor(0xFF, 0x80, 0x80, 0x80));
        Brush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    public string Hex { get; }

    public SolidColorBrush Brush { get; }

    /// <summary>Selects this color; the command parameter is <see cref="Hex"/>.</summary>
    public ICommand SelectCommand { get; }
}

public sealed class LayoutPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _widgets;

    public LayoutPageViewModel(ISettingsService settings, IWidgetRegistry widgets)
        : base("Layout", "\uE8FD", "Order the sections of the bar and choose the zone each one lives in.")
    {
        _settings = settings;
        _widgets = widgets;
        Reload();
    }

    public ObservableCollection<LayoutItemViewModel> Sections { get; } = new();

    public IReadOnlyList<TaskbarZone> Zones { get; } = Enum.GetValues<TaskbarZone>();

    /// <summary>Display names of <see cref="Zones"/>, in the same order (indexes match).</summary>
    public IReadOnlyList<string> ZoneNames { get; } = Enum.GetNames<TaskbarZone>();

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Sections.Clear();
        var settings = _settings.Current;
        foreach (var s in settings.Layout)
        {
            Sections.Add(new LayoutItemViewModel(this, s.Id, Describe(s, settings), s.Zone));
        }
    }

    private string Describe(SectionPlacement s, SuperBarSettings settings) => s.Kind switch
    {
        SectionKind.Start => "Start button",
        SectionKind.Applications => "Applications (pinned and running)",
        SectionKind.SystemTray => "System tray",
        SectionKind.Clock => "Clock and date",
        SectionKind.Widget => settings.Widgets.FirstOrDefault(w => w.InstanceId == s.WidgetInstanceId) is { } w && _widgets.TryGet(w.TypeId, out var d)
            ? $"Widget: {d.DisplayName}"
            : "Widget",
        _ => s.Kind.ToString(),
    };

    internal void Move(LayoutItemViewModel item, int delta)
    {
        _settings.Update(s =>
        {
            var index = s.Layout.FindIndex(p => p.Id == item.Id);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= s.Layout.Count)
            {
                return;
            }

            (s.Layout[index], s.Layout[target]) = (s.Layout[target], s.Layout[index]);
        });
        Reload();
    }

    internal void SetZone(LayoutItemViewModel item, TaskbarZone zone) =>
        _settings.Update(s =>
        {
            if (s.Layout.FirstOrDefault(p => p.Id == item.Id) is { } placement)
            {
                placement.Zone = zone;
            }
        });
}

public sealed class LayoutItemViewModel : ObservableObject
{
    private readonly LayoutPageViewModel _owner;
    private TaskbarZone _zone;

    public LayoutItemViewModel(LayoutPageViewModel owner, string id, string name, TaskbarZone zone)
    {
        _owner = owner;
        Id = id;
        Name = name;
        _zone = zone;
        MoveUpCommand = new RelayCommand(() => _owner.Move(this, -1));
        MoveDownCommand = new RelayCommand(() => _owner.Move(this, +1));
    }

    public string Id { get; }
    public string Name { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    /// <summary>The zones this section can be placed in (exposed per item, so the ComboBox needs no ancestor binding).</summary>
    public IReadOnlyList<string> Zones => _owner.ZoneNames;

    /// <summary>Index of <see cref="Zone"/> in <see cref="Zones"/>; bound two-way to the ComboBox.</summary>
    public int ZoneIndex
    {
        get => IndexOf(_owner.Zones, _zone);
        set
        {
            var zones = _owner.Zones;
            if (value >= 0 && value < zones.Count)
            {
                Zone = zones[value];
            }
        }
    }

    private static int IndexOf(IReadOnlyList<TaskbarZone> zones, TaskbarZone zone)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i] == zone)
            {
                return i;
            }
        }

        return -1;
    }

    public TaskbarZone Zone
    {
        get => _zone;
        set
        {
            if (SetProperty(ref _zone, value))
            {
                OnPropertyChanged(nameof(ZoneIndex));
                _owner.SetZone(this, value);
            }
        }
    }
}

public sealed class WidgetsPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _registry;
    private readonly IUiDispatcher _dispatcher;

    public WidgetsPageViewModel(ISettingsService settings, IWidgetRegistry registry, IUiDispatcher dispatcher)
        : base("Widgets", "\uE71D", "Widgets live directly inside the bar. Enable them and size their slots.")
    {
        _settings = settings;
        _registry = registry;
        _dispatcher = dispatcher;
        Reload();
    }

    public ObservableCollection<WidgetItemViewModel> Instances { get; } = new();

    public IReadOnlyList<WidgetDescriptor> AvailableTypes => _registry.All.ToList();

    /// <summary>Bulleted names of <see cref="AvailableTypes"/>.</summary>
    public IReadOnlyList<string> AvailableTypeLabels => _registry.All.Select(d => $"• {d.DisplayName}").ToList();

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Instances.Clear();
        foreach (var w in _settings.Current.Widgets)
        {
            if (_registry.TryGet(w.TypeId, out var d))
            {
                Instances.Add(new WidgetItemViewModel(_settings, _dispatcher, w.InstanceId, d, w.Enabled, d.ResolveWidth(w.Width)));
            }
        }
    }
}

public sealed class WidgetItemViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce;
    private bool _enabled;
    private double _width;

    public WidgetItemViewModel(ISettingsService settings, IUiDispatcher dispatcher, string instanceId, WidgetDescriptor descriptor, bool enabled, double width)
    {
        _settings = settings;
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(120));
        InstanceId = instanceId;
        Descriptor = descriptor;
        _enabled = enabled;
        _width = width;
    }

    public string InstanceId { get; }
    public WidgetDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public double MinWidth => Descriptor.MinWidth;
    public double MaxWidth => Descriptor.MaxWidth;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _settings.Update(s => Find(s)!.Enabled = value);
            }
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, Math.Round(value)))
            {
                OnPropertyChanged(nameof(WidthLabel));
                var w = _width;
                _debounce.Run(() => _settings.Update(s => Find(s)!.Width = w));
            }
        }
    }

    public string WidthLabel => AppearancePageViewModel.FormatPx(_width);

    private WidgetInstanceSettings? Find(SuperBarSettings s) => s.Widgets.FirstOrDefault(x => x.InstanceId == InstanceId);
}

public sealed class ApplicationsPageViewModel : PageViewModel
{
    public ApplicationsPageViewModel(ISettingsService settings)
        : base("Applications", "\uECAA", "Pinned applications shown on the bar.")
    {
        Settings = settings;
        Reload();
    }

    private ISettingsService Settings { get; }

    public ObservableCollection<PinnedApp> Pinned { get; } = new();

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Pinned.Clear();
        foreach (var p in Settings.Current.PinnedApps)
        {
            Pinned.Add(p);
        }
    }
}

public sealed class AboutPageViewModel : PageViewModel
{
    public AboutPageViewModel(IAppPaths paths, IAppLauncher launcher)
        : base("About", "\uE946", "Version, data locations and diagnostics.")
    {
        DataDirectory = paths.DataDirectory;
        LogDirectory = paths.LogDirectory;
        OpenDataFolderCommand = new RelayCommand(() => launcher.Launch(paths.DataDirectory));
        OpenLogFolderCommand = new RelayCommand(() => launcher.Launch(paths.LogDirectory));
    }

    public string Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string VersionLabel => $"SuperBar {Version} - foundation milestone";
    public string DataDirectoryLabel => $"Data: {DataDirectory}";
    public string LogDirectoryLabel => $"Logs: {LogDirectory}";
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
}
