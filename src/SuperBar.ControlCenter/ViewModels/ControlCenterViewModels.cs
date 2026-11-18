using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using Avalonia.Threading;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter.ViewModels;

public sealed class ControlCenterViewModel : ObservableObject
{
    private PageViewModel _selectedPage;

    public ControlCenterViewModel(ISettingsService settings, ITaskbarRuntimeController runtime, IWidgetRegistry widgets, IAppPaths paths, IAppLauncher launcher)
    {
        Pages =
        [
            new GeneralPageViewModel(settings, runtime),
            new AppearancePageViewModel(settings),
            new LayoutPageViewModel(settings, widgets),
            new WidgetsPageViewModel(settings, widgets),
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
}

public abstract class PageViewModel(string title, string glyph, string subtitle) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Subtitle { get; } = subtitle;

    public virtual void OnNavigatedTo()
    {
    }
}

/// <summary>Coalesces rapid edits (slider drags) into one settings write.</summary>
internal sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Action? _pending;

    public Debouncer(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var action = _pending;
            _pending = null;
            action?.Invoke();
        };
    }

    public void Run(Action action)
    {
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }
}

// ------------------------------------------------------------------------------------------------

public sealed class GeneralPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ITaskbarRuntimeController _runtime;
    private bool _hideNativeTaskbar;

    public GeneralPageViewModel(ISettingsService settings, ITaskbarRuntimeController runtime)
        : base("General", "M3,3 H13 V13 H3 Z M6,6 H10 V10 H6 Z", "Turn SuperBar on or off and control how it replaces the Windows taskbar.")
    {
        _settings = settings;
        _runtime = runtime;
        _hideNativeTaskbar = settings.Current.Behavior.HideNativeTaskbar;
        runtime.StateChanged += (_, _) => OnPropertyChanged(nameof(IsSuperBarEnabled));
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
}

public sealed class AppearancePageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce = new(TimeSpan.FromMilliseconds(120));
    private double _height;
    private double _opacity;
    private double _cornerRadius;
    private double _spacing;
    private bool _darkTheme;
    private bool _useAcrylic;
    private string _accent;

    public AppearancePageViewModel(ISettingsService settings)
        : base("Appearance", "M8,2 A6,6 0 1 0 8,14 A3,3 0 0 1 8,8 A3,3 0 0 0 8,2 Z", "Size, transparency, corners and colors of the bar.")
    {
        _settings = settings;
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
    }

    public double MinHeight => AppearanceSettings.MinHeight;
    public double MaxHeight => AppearanceSettings.MaxHeight;

    public IReadOnlyList<string> AccentSwatches { get; } = ["#5B8CFF", "#8B6CFF", "#2FBF9B", "#F2A541", "#F2545B", "#E8E8EC"];

    public ICommand SetAccentCommand { get; }

    public double TaskbarHeight { get => _height; set { if (SetProperty(ref _height, Math.Round(value))) Push(s => s.Appearance.Height = _height); } }

    public double BackgroundOpacity { get => _opacity; set { if (SetProperty(ref _opacity, value)) Push(s => s.Appearance.BackgroundOpacity = _opacity); } }

    public double CornerRadius { get => _cornerRadius; set { if (SetProperty(ref _cornerRadius, Math.Round(value))) Push(s => s.Appearance.ItemCornerRadius = _cornerRadius); } }

    public double ItemSpacing { get => _spacing; set { if (SetProperty(ref _spacing, Math.Round(value))) Push(s => s.Appearance.ItemSpacing = _spacing); } }

    public bool DarkTheme { get => _darkTheme; set { if (SetProperty(ref _darkTheme, value)) Push(s => s.Appearance.Theme = value ? ThemeMode.Dark : ThemeMode.Light); } }

    public bool UseAcrylic { get => _useAcrylic; set { if (SetProperty(ref _useAcrylic, value)) Push(s => s.Appearance.UseAcrylic = value); } }

    public string Accent { get => _accent; set { if (SetProperty(ref _accent, value)) Push(s => s.Appearance.AccentColor = value); } }

    private void Push(Action<SuperBarSettings> change) => _debounce.Run(() => _settings.Update(change));
}

public sealed class LayoutPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _widgets;

    public LayoutPageViewModel(ISettingsService settings, IWidgetRegistry widgets)
        : base("Layout", "M2,4 H14 M2,8 H14 M2,12 H14", "Order the sections of the bar and choose the zone each one lives in.")
    {
        _settings = settings;
        _widgets = widgets;
        Reload();
    }

    public ObservableCollection<LayoutItemViewModel> Sections { get; } = new();

    public IReadOnlyList<TaskbarZone> Zones { get; } = Enum.GetValues<TaskbarZone>();

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

    public TaskbarZone Zone
    {
        get => _zone;
        set
        {
            if (SetProperty(ref _zone, value))
            {
                _owner.SetZone(this, value);
            }
        }
    }
}

public sealed class WidgetsPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _registry;

    public WidgetsPageViewModel(ISettingsService settings, IWidgetRegistry registry)
        : base("Widgets", "M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z", "Widgets live directly inside the bar. Enable them and size their slots.")
    {
        _settings = settings;
        _registry = registry;
        Reload();
    }

    public ObservableCollection<WidgetItemViewModel> Instances { get; } = new();

    public IReadOnlyList<WidgetDescriptor> AvailableTypes => _registry.All.ToList();

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Instances.Clear();
        foreach (var w in _settings.Current.Widgets)
        {
            if (_registry.TryGet(w.TypeId, out var d))
            {
                Instances.Add(new WidgetItemViewModel(_settings, w.InstanceId, d, w.Enabled, d.ResolveWidth(w.Width)));
            }
        }
    }
}

public sealed class WidgetItemViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce = new(TimeSpan.FromMilliseconds(120));
    private bool _enabled;
    private double _width;

    public WidgetItemViewModel(ISettingsService settings, string instanceId, WidgetDescriptor descriptor, bool enabled, double width)
    {
        _settings = settings;
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
                var w = _width;
                _debounce.Run(() => _settings.Update(s => Find(s)!.Width = w));
            }
        }
    }

    private WidgetInstanceSettings? Find(SuperBarSettings s) => s.Widgets.FirstOrDefault(x => x.InstanceId == InstanceId);
}

public sealed class ApplicationsPageViewModel : PageViewModel
{
    public ApplicationsPageViewModel(ISettingsService settings)
        : base("Applications", "M3,2 H13 V14 H3 Z M3,5 H13", "Pinned applications shown on the bar.")
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
        : base("About", "M8,2 A6,6 0 1 0 8.01,2 Z M8,7 V11 M8,5 V5.1", "Version, data locations and diagnostics.")
    {
        DataDirectory = paths.DataDirectory;
        LogDirectory = paths.LogDirectory;
        OpenDataFolderCommand = new RelayCommand(() => launcher.Launch(paths.DataDirectory));
        OpenLogFolderCommand = new RelayCommand(() => launcher.Launch(paths.LogDirectory));
    }

    public string Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
}
