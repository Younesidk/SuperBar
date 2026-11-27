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

/// <summary>What the Control Center says about the running application.</summary>
public static class AppInfo
{
    /// <summary>The application version, e.g. "0.1.0" (the entry assembly's; set once in Directory.Build.props).</summary>
    public static string Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
}

/// <summary>The window's shell: the pages, the selected one, and the navigation pane's status footer.</summary>
public sealed class ControlCenterViewModel : ObservableObject, IDisposable
{
    private readonly ITaskbarRuntimeController _runtime;
    private PageViewModel _selectedPage;

    public ControlCenterViewModel(
        ISettingsService settings,
        ITaskbarRuntimeController runtime,
        IWidgetRegistry widgets,
        IWidgetSettingsEditors settingsEditors,
        ISettingsStore store,
        IAppPaths paths,
        IAppLauncher launcher,
        IIconProvider icons,
        INativeTaskbarController nativeTaskbar,
        IControlCenterUi ui,
        IUiDispatcher dispatcher)
    {
        _runtime = runtime;
        _runtime.StateChanged += OnRuntimeStateChanged;
        ExitCommand = new RelayCommand(runtime.Exit);

        Pages = (PageViewModel[])
        [
            new GeneralPageViewModel(settings, runtime, ui),
            new AppearancePageViewModel(settings, dispatcher),
            new LayoutPageViewModel(settings, widgets),
            new WidgetsPageViewModel(settings, widgets, settingsEditors, store, dispatcher),
            new ApplicationsPageViewModel(settings, icons, dispatcher),
            new AboutPageViewModel(paths, launcher, settings, nativeTaskbar, ui, dispatcher),
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

    // ----- Status footer of the navigation pane -----

    /// <summary>True while the bar is shown; false while SuperBar is paused (the process keeps running).</summary>
    public bool IsActive => _runtime.IsEnabled;

    public bool IsPaused => !_runtime.IsEnabled;

    public string StatusText => IsActive ? "SuperBar Active" : "Paused";

    public string StatusDescription => IsActive
        ? "SuperBar is running in place of the Windows taskbar."
        : "SuperBar is paused and the Windows taskbar is back. Turn it on again under General.";

    public string VersionLabel { get; } = "v" + AppInfo.Version;

    /// <summary>Restores the Windows taskbar and ends the application.</summary>
    public ICommand ExitCommand { get; }

    // Raised on the UI thread: the runtime is only enabled and disabled there.
    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDescription));
    }

    /// <summary>Detaches the shell and the pages from long-lived services when the window closes.</summary>
    public void Dispose()
    {
        _runtime.StateChanged -= OnRuntimeStateChanged;
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
    private readonly IControlCenterUi _ui;
    private bool _hideNativeTaskbar;

    public GeneralPageViewModel(ISettingsService settings, ITaskbarRuntimeController runtime, IControlCenterUi ui)
        : base("General", "\uE713", "Turn SuperBar on or off and control how it replaces the Windows taskbar.")
    {
        _settings = settings;
        _runtime = runtime;
        _ui = ui;
        _hideNativeTaskbar = settings.Current.Behavior.HideNativeTaskbar;
        runtime.StateChanged += OnRuntimeStateChanged;
        ResetCommand = new RelayCommand(ConfirmAndReset);
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

    /// <summary>Asks first, then puts every setting back to its default.</summary>
    public ICommand ResetCommand { get; }

    // The About page's "Restore Windows taskbar" turns HideNativeTaskbar off while this view model stays alive.
    public override void OnNavigatedTo() => SyncFromSettings();

    private async void ConfirmAndReset()
    {
        try
        {
            var confirmed = await _ui.ConfirmAsync(
                "Reset all settings?",
                "Appearance, layout, widgets and pinned applications go back to their defaults. This cannot be undone.",
                "Reset",
                destructive: true);
            if (!confirmed)
            {
                return;
            }

            _settings.ResetToDefaults();
            SyncFromSettings();
        }
        catch (Exception)
        {
            // async void: nothing may escape. A dialog that fails to show resets nothing.
        }
    }

    /// <summary>Re-reads the cached setting without writing it back (the property setter would call Update).</summary>
    private void SyncFromSettings()
    {
        var hide = _settings.Current.Behavior.HideNativeTaskbar;
        if (hide != _hideNativeTaskbar)
        {
            _hideNativeTaskbar = hide;
            OnPropertyChanged(nameof(HideNativeTaskbar));
        }
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IsSuperBarEnabled));

    public void Dispose() => _runtime.StateChanged -= OnRuntimeStateChanged;
}

public sealed class AppearancePageViewModel : PageViewModel
{
    private static readonly (string Name, string Hex)[] SwatchColors =
    [
        ("Blue", "#5B8CFF"),
        ("Violet", "#8B6CFF"),
        ("Teal", "#2FBF9B"),
        ("Amber", "#F2A541"),
        ("Red", "#F2545B"),
        ("White", "#E8E8EC"),
    ];

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
        : base("Appearance", "\uE790", "Size, transparency, corners and colors of the bar.")
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
        AccentSwatches = SwatchColors.Select(c => new AccentSwatchViewModel(c.Name, c.Hex, SetAccentCommand)).ToList();
        UpdateSwatchSelection();
    }

    /// <summary>
    /// Re-reads every value from the settings without writing anything back: "Reset all settings" on the General page
    /// changes them while this view model stays alive. Skipped while this page has edits of its own still queued,
    /// because those are newer than the stored values.
    /// </summary>
    public override void OnNavigatedTo()
    {
        if (_pendingChanges is not null)
        {
            return;
        }

        var a = _settings.Current.Appearance;
        _height = a.Height;
        _opacity = a.BackgroundOpacity;
        _cornerRadius = a.ItemCornerRadius;
        _spacing = a.ItemSpacing;
        _darkTheme = a.Theme == ThemeMode.Dark;
        _useAcrylic = a.UseAcrylic;
        _accent = a.AccentColor;

        OnPropertyChanged(nameof(TaskbarHeight));
        OnPropertyChanged(nameof(TaskbarHeightLabel));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BackgroundOpacityLabel));
        OnPropertyChanged(nameof(CornerRadius));
        OnPropertyChanged(nameof(CornerRadiusLabel));
        OnPropertyChanged(nameof(ItemSpacing));
        OnPropertyChanged(nameof(ItemSpacingLabel));
        OnPropertyChanged(nameof(DarkTheme));
        OnPropertyChanged(nameof(UseAcrylic));
        OnPropertyChanged(nameof(Accent));
        UpdateSwatchSelection();
    }

    // A stored accent that matches no swatch (edited by hand) leaves none selected.
    private void UpdateSwatchSelection()
    {
        foreach (var swatch in AccentSwatches)
        {
            swatch.IsSelected = string.Equals(swatch.Hex, _accent, StringComparison.OrdinalIgnoreCase);
        }
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

    public string Accent
    {
        get => _accent;
        set
        {
            if (SetProperty(ref _accent, value))
            {
                UpdateSwatchSelection();
                Push(s => s.Appearance.AccentColor = value);
            }
        }
    }

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

/// <summary>
/// One accent color button: its own brush, command and selection state, so the template needs no ancestor binding.
/// </summary>
public sealed class AccentSwatchViewModel : ObservableObject
{
    // Swatches at least this bright get a black check mark, darker ones a white one.
    private const double LightLuminance = 0.6;

    private bool _isSelected;

    public AccentSwatchViewModel(string name, string hex, ICommand selectCommand)
    {
        Name = name;
        Hex = hex;
        SelectCommand = selectCommand;
        var c = ColorHex.ParseOrDefault(hex, new ArgbColor(0xFF, 0x80, 0x80, 0x80));
        Brush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
        CheckBrush = new SolidColorBrush(RelativeLuminance(c) >= LightLuminance
            ? global::Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>The color's friendly name ("Blue"), for the tooltip and screen readers.</summary>
    public string Name { get; }

    public string Hex { get; }

    public SolidColorBrush Brush { get; }

    /// <summary>The check mark's color: readable on this swatch (black on light colors, white on dark ones).</summary>
    public SolidColorBrush CheckBrush { get; }

    public string AccessibleName => Name;

    /// <summary>True for the swatch of the current accent color.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Selects this color; the command parameter is <see cref="Hex"/>.</summary>
    public ICommand SelectCommand { get; }

    /// <summary>WCAG relative luminance (0 black .. 1 white) of an sRGB color.</summary>
    private static double RelativeLuminance(ArgbColor c)
    {
        static double Linear(byte channel)
        {
            var v = channel / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }
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

public sealed class ApplicationsPageViewModel : PageViewModel, IDisposable
{
    private const int IconPixelSize = 64; // crisp at 200% for the 32 DIP icon

    private readonly ISettingsService _settings;
    private readonly IIconProvider _icons;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";

    public ApplicationsPageViewModel(ISettingsService settings, IIconProvider icons, IUiDispatcher dispatcher)
        : base("Applications", "\uECAA", "Pinned applications shown on the bar. Pin or unpin from here or by right-clicking an app on the bar; drag apps on the bar to reorder them.")
    {
        _settings = settings;
        _icons = icons;
        _dispatcher = dispatcher;
        _settings.Changed += OnSettingsChanged;
        Reload();
    }

    public ObservableCollection<PinnedAppItemViewModel> Pinned { get; } = new();

    public bool IsEmpty => Pinned.Count == 0;

    public override void OnNavigatedTo() => Reload();

    // Pins change from the bar (Pin / Unpin, drag to reorder) while this page is open: follow live.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _dispatcher.Invoke(Reload);

    private void Reload()
    {
        var pins = _settings.Current.PinnedApps;
        var signature = string.Join("|", pins.Select(p => p.Id + "=" + p.Path + "=" + p.DisplayName));
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        Pinned.Clear();
        foreach (var p in pins)
        {
            Pinned.Add(new PinnedAppItemViewModel(p, IconFor(p.Path), Unpin));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private ImageSource? IconFor(string path)
    {
        if (!_iconCache.TryGetValue(path, out var icon))
        {
            icon = IconImages.ToBitmap(_icons.GetFileIcon(path, IconPixelSize));
            _iconCache[path] = icon;
        }

        return icon;
    }

    private void Unpin(PinnedAppItemViewModel item) =>
        _settings.Update(s => s.PinnedApps.RemoveAll(p => p.Id == item.Id));

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}

/// <summary>One pinned application on the Applications page.</summary>
public sealed class PinnedAppItemViewModel
{
    public PinnedAppItemViewModel(PinnedApp pin, ImageSource? icon, Action<PinnedAppItemViewModel> unpin)
    {
        Id = pin.Id;
        DisplayName = string.IsNullOrWhiteSpace(pin.DisplayName) ? Path.GetFileNameWithoutExtension(pin.Path) : pin.DisplayName;
        AppPath = pin.Path;
        Icon = icon;
        UnpinCommand = new RelayCommand(() => unpin(this));
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string AppPath { get; }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    /// <summary>Shown instead of the icon when the executable is missing or has none.</summary>
    public bool ShowFallbackIcon => Icon is null;

    public ICommand UnpinCommand { get; }
}

public sealed class AboutPageViewModel : PageViewModel
{
    private const string CopyGlyphIdle = "\uE8C8";
    private const string CopyGlyphDone = "\uE73E";

    private readonly ISettingsService _settings;
    private readonly INativeTaskbarController _nativeTaskbar;
    private readonly IControlCenterUi _ui;
    private readonly Debouncer _copiedReset;
    private bool _isCopied;
    private string _restoreStatus = "";

    public AboutPageViewModel(
        IAppPaths paths,
        IAppLauncher launcher,
        ISettingsService settings,
        INativeTaskbarController nativeTaskbar,
        IControlCenterUi ui,
        IUiDispatcher dispatcher)
        : base("About", "\uE946", "Version, data locations and diagnostics.")
    {
        _settings = settings;
        _nativeTaskbar = nativeTaskbar;
        _ui = ui;
        _copiedReset = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(1500));
        DataDirectory = paths.DataDirectory;
        LogDirectory = paths.LogDirectory;
        OpenDataFolderCommand = new RelayCommand(() => launcher.Launch(paths.DataDirectory));
        OpenLogFolderCommand = new RelayCommand(() => launcher.Launch(paths.LogDirectory));
        CopyRestoreCommand = new RelayCommand(CopyRestore);
        RestoreTaskbarCommand = new RelayCommand(RestoreTaskbar);
    }

    public string Version { get; } = AppInfo.Version;
    public string VersionPill => "v" + Version;
    public string Tagline => "A modern replacement for the Windows taskbar. Foundation milestone.";
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DataDirectoryLabel => $"Data: {DataDirectory}";
    public string LogDirectoryLabel => $"Logs: {LogDirectory}";
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>The command line that brings a stuck Windows taskbar back.</summary>
    public string RestoreCommandText => "SuperBar.exe --restore-taskbar";

    /// <summary>Copies <see cref="RestoreCommandText"/>; the button shows a check mark for a moment.</summary>
    public ICommand CopyRestoreCommand { get; }

    /// <summary>True for a short while after <see cref="CopyRestoreCommand"/>.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        private set
        {
            if (SetProperty(ref _isCopied, value))
            {
                OnPropertyChanged(nameof(CopyGlyph));
            }
        }
    }

    public string CopyGlyph => _isCopied ? CopyGlyphDone : CopyGlyphIdle;

    /// <summary>Brings the Windows taskbar back now and turns "Hide the Windows taskbar" off. No confirmation: the safe direction.</summary>
    public ICommand RestoreTaskbarCommand { get; }

    /// <summary>What the last <see cref="RestoreTaskbarCommand"/> did; empty until it runs.</summary>
    public string RestoreStatus
    {
        get => _restoreStatus;
        private set
        {
            if (SetProperty(ref _restoreStatus, value))
            {
                OnPropertyChanged(nameof(HasRestoreStatus));
            }
        }
    }

    public bool HasRestoreStatus => _restoreStatus.Length > 0;

    private void CopyRestore()
    {
        _ui.CopyText(RestoreCommandText);
        IsCopied = true;
        _copiedReset.Run(() => IsCopied = false);
    }

    private void RestoreTaskbar()
    {
        // Turning the setting off is what makes the runtime restore the taskbar and move the bar off it; calling the
        // controller alone would leave the setting on and the bar overlapping the taskbar.
        if (_settings.Current.Behavior.HideNativeTaskbar)
        {
            _settings.Update(s => s.Behavior.HideNativeTaskbar = false);
        }

        // Idempotent; also covers a taskbar left hidden while SuperBar is paused.
        _nativeTaskbar.Restore();
        RestoreStatus = "The Windows taskbar is back. 'Hide the Windows taskbar' is now off; turn it on again under General.";
    }
}
