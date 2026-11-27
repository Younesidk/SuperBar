using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Layout;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Services;

namespace SuperBar.Runtime.ViewModels;

/// <summary>
/// Pinned and running applications. Running windows are grouped by executable path and matched to pins
/// by path, so a pinned app lights up when it runs. Updates are diffed to avoid re-creating buttons.
/// <para>
/// The user can drag buttons into any order. That order is kept for the session (running apps included) and the
/// pinned apps' order is persisted through <see cref="PinOrderChanged"/>.
/// </para>
/// </summary>
public sealed class AppsSectionViewModel : SectionViewModel
{
    private readonly IWindowTracker _tracker;
    private readonly IWindowSwitcher _switcher;
    private readonly IAppLauncher _launcher;
    private readonly IconCache _icons;
    private readonly ILogger _logger;
    private IReadOnlyList<PinnedApp> _pins = [];
    private bool _active;
    private List<string> _manualOrder = [];
    private bool _reordering;
    private bool _rebuildPending;
    private DateTime _animateAfter = DateTime.MaxValue;
    private readonly List<AppButtonViewModel> _finishedLeaving = [];

    /// <summary>Raised after a drag changed the order: the keys of the pinned apps, in their new order.</summary>
    public event Action<IReadOnlyList<string>>? PinOrderChanged;

    /// <summary>"Pin to taskbar": the new pin, and its position among the pinned apps.</summary>
    public event Action<PinnedApp, int>? PinRequested;

    /// <summary>"Unpin from taskbar": the key (lower-cased path) of the pin to remove.</summary>
    public event Action<string>? UnpinRequested;

    public AppsSectionViewModel(string id, IWindowTracker tracker, IWindowSwitcher switcher, IAppLauncher launcher, IconCache icons, ILogger logger) : base(id)
    {
        _tracker = tracker;
        _switcher = switcher;
        _launcher = launcher;
        _icons = icons;
        _logger = logger;
    }

    /// <summary>Right after activation the tracker is still reporting the windows that were already open.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(2);

    public ObservableCollection<AppButtonViewModel> Apps { get; } = new();

    /// <summary>A button is appearing or disappearing: the row is changing width, so a drag waits.</summary>
    public bool IsAnimating => Apps.Any(a => a.IsLeaving || a.IsAnimating);

    public void SetPins(IReadOnlyList<PinnedApp> pins)
    {
        _pins = pins;
        Rebuild();
    }

    public override void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _tracker.WindowsChanged += OnWindowsChanged;
        Rebuild();
        _animateAfter = DateTime.UtcNow + StartupGrace;
    }

    public override void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _tracker.WindowsChanged -= OnWindowsChanged;
    }

    private void OnWindowsChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>A button is being dragged: structural updates wait until it is dropped.</summary>
    public void BeginReorder() => _reordering = true;

    /// <summary>Ends a drag, moving the button from <paramref name="from"/> to <paramref name="to"/> (equal to cancel).</summary>
    public void EndReorder(int from, int to)
    {
        _reordering = false;
        if (from != to && from >= 0 && to >= 0 && from < Apps.Count && to < Apps.Count)
        {
            Apps.Move(from, to);
            _manualOrder = Apps.Where(a => !a.IsLeaving).Select(a => a.Key).ToList();
            _logger.LogInformation("Moved {App} to position {Position}", Apps[to].DisplayName, to);
            PinOrderChanged?.Invoke(Apps.Where(a => a.IsPinned && !a.IsLeaving).Select(a => a.Key).ToList());
        }

        if (_rebuildPending)
        {
            _rebuildPending = false;
            Rebuild();
        }
    }

    internal static string GroupKey(TrackedWindow w) =>
        string.IsNullOrEmpty(w.ProcessPath) ? $"hwnd:{w.Handle}" : w.ProcessPath.ToLowerInvariant();

    private void Rebuild()
    {
        if (_reordering)
        {
            _rebuildPending = true;
            return;
        }

        foreach (var vm in _finishedLeaving)
        {
            if (vm.IsLeaving)
            {
                Apps.Remove(vm);
            }
        }

        _finishedLeaving.Clear();

        var windowsByKey = new Dictionary<string, List<TrackedWindow>>();
        var runningOrder = new List<string>();
        foreach (var w in _tracker.Windows)
        {
            var key = GroupKey(w);
            if (!windowsByKey.TryGetValue(key, out var list))
            {
                windowsByKey[key] = list = new List<TrackedWindow>();
                runningOrder.Add(key);
            }

            list.Add(w);
        }

        var desired = new List<(string Key, PinnedApp? Pin)>();
        var seen = new HashSet<string>();
        foreach (var pin in _pins)
        {
            var key = pin.Path.ToLowerInvariant();
            if (seen.Add(key))
            {
                desired.Add((key, pin));
            }
        }

        foreach (var key in runningOrder)
        {
            if (seen.Add(key))
            {
                desired.Add((key, null));
            }
        }

        desired = AppOrdering.Arrange(desired, d => d.Key, _manualOrder);

        // The buttons the bar starts with appear at once; apps opened or closed later animate.
        var animate = DateTime.UtcNow >= _animateAfter;
        var existing = Apps.ToDictionary(a => a.Key);
        var active = _tracker.ActiveWindow;
        var target = new List<AppButtonViewModel>(desired.Count);
        foreach (var (key, pin) in desired)
        {
            windowsByKey.TryGetValue(key, out var windows);
            windows ??= [];

            if (!existing.TryGetValue(key, out var vm))
            {
                vm = new AppButtonViewModel(key, this) { AnimateIn = animate };
            }

            vm.IsLeaving = false; // reopened while it was animating out
            vm.Update(pin, windows, active, ResolveIcon(pin, windows), _icons.ForPixels(ResolveBadge(windows, active)));
            target.Add(vm);
        }

        // Closed apps keep their slot while they animate out (CompleteRemoval takes them off).
        var kept = new HashSet<AppButtonViewModel>(target);
        AppButtonViewModel? previous = null;
        foreach (var vm in Apps)
        {
            if (!kept.Contains(vm) && animate)
            {
                vm.IsLeaving = true;
                target.Insert(previous is null ? 0 : target.IndexOf(previous) + 1, vm);
            }

            if (target.Contains(vm))
            {
                previous = vm;
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var vm = target[i];
            var currentIndex = Apps.IndexOf(vm);
            if (currentIndex < 0)
            {
                Apps.Insert(i, vm);
            }
            else if (currentIndex != i)
            {
                Apps.Move(currentIndex, i);
            }
        }

        while (Apps.Count > target.Count)
        {
            Apps.RemoveAt(Apps.Count - 1);
        }
    }

    internal void CompleteRemoval(AppButtonViewModel vm)
    {
        if (!vm.IsLeaving)
        {
            return;
        }

        if (_reordering)
        {
            _rebuildPending = true; // indexes must stay put until the drop; the next rebuild drops it
            _finishedLeaving.Add(vm);
            return;
        }

        Apps.Remove(vm);
    }

    /// <summary>
    /// The badge a grouped button shows: the active window's, else the first one any of its windows set
    /// (apps such as Discord badge only their main window).
    /// </summary>
    internal static IconPixels? ResolveBadge(IReadOnlyList<TrackedWindow> windows, nint activeWindow) =>
        windows.FirstOrDefault(w => w.Handle == activeWindow && w.OverlayIcon is not null)?.OverlayIcon
        ?? windows.FirstOrDefault(w => w.OverlayIcon is not null)?.OverlayIcon;

    private ImageSource? ResolveIcon(PinnedApp? pin, List<TrackedWindow> windows)
    {
        if (pin is not null)
        {
            return _icons.ForFile(pin.Path);
        }

        var first = windows.FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        // UWP apps run inside ApplicationFrameHost; its file icon is generic, the window icon is better.
        var path = first.ProcessPath;
        if (!string.IsNullOrEmpty(path) && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return _icons.ForFile(path) ?? _icons.ForWindow(first.Handle);
        }

        return _icons.ForWindow(first.Handle);
    }

    internal void OnClicked(AppButtonViewModel app)
    {
        if (app.Windows.Count == 0)
        {
            if (app.LaunchPath is { } path)
            {
                _launcher.Launch(path, app.LaunchArguments);
            }

            return;
        }

        var active = _tracker.ActiveWindow;
        if (app.Windows.Count == 1)
        {
            var w = app.Windows[0].Handle;
            _switcher.ActivateOrMinimize(w, w == active);
            return;
        }

        // Multiple windows: cycle through them (a thumbnail picker is a future milestone).
        var index = app.Windows.FindIndex(w => w.Handle == active);
        var next = app.Windows[(index + 1) % app.Windows.Count].Handle;
        _logger.LogDebug("Cycling {App} to window 0x{Handle:X}", app.DisplayName, next);
        _switcher.Activate(next);
    }

    internal void OnTogglePin(AppButtonViewModel app)
    {
        // Freeze the current order first: pinning or unpinning must not make any button jump.
        _manualOrder = Apps.Select(a => a.Key).ToList();

        if (app.IsPinned)
        {
            _logger.LogInformation("Unpinning {App}", app.DisplayName);
            UnpinRequested?.Invoke(app.Key);
            return;
        }

        if (!app.CanPin || app.LaunchPath is not { } path)
        {
            return;
        }

        var index = Apps.TakeWhile(a => !ReferenceEquals(a, app)).Count(a => a.IsPinned);
        var pin = new PinnedApp { DisplayName = FriendlyName(path, app.DisplayName), Path = path };
        _logger.LogInformation("Pinning {App} ({Path}) at position {Index}", pin.DisplayName, path, index);
        PinRequested?.Invoke(pin, index);
    }

    /// <summary>The executable's description ("Notepad", "JetBrains Rider") rather than its file name.</summary>
    internal static string FriendlyName(string path, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
            return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    internal void OnLaunchNew(AppButtonViewModel app)
    {
        if (app.LaunchPath is { } path)
        {
            _launcher.Launch(path, app.LaunchArguments);
        }
    }
}

public sealed class AppButtonViewModel : ObservableObject
{
    private readonly AppsSectionViewModel _owner;
    private string _displayName = "";
    private string? _toolTip;
    private readonly RelayCommand _launchNewCommand;
    private readonly RelayCommand _togglePinCommand;
    private ImageSource? _icon;
    private bool _isPinned;
    private bool _isRunning;
    private bool _isActive;
    private bool _hasMultipleWindows;
    private bool _isHovered;
    private bool _isFlashing;
    private ImageSource? _badge;
    private bool _isLeaving;

    public AppButtonViewModel(string key, AppsSectionViewModel owner)
    {
        Key = key;
        _owner = owner;
        ClickCommand = new RelayCommand(() => _owner.OnClicked(this));
        _launchNewCommand = new RelayCommand(_ => _owner.OnLaunchNew(this), _ => CanLaunch);
        _togglePinCommand = new RelayCommand(_ => _owner.OnTogglePin(this), _ => IsPinned || CanPin);
    }

    public string Key { get; }

    public ICommand ClickCommand { get; }

    /// <summary>"Open new window". Disabled through CanExecute when there is nothing to launch.</summary>
    public ICommand LaunchNewCommand => _launchNewCommand;

    /// <summary>"Pin to taskbar" / "Unpin from taskbar".</summary>
    public ICommand TogglePinCommand => _togglePinCommand;

    /// <summary>
    /// A running app can be pinned when it has a launchable executable. Store (UWP) apps run inside
    /// ApplicationFrameHost.exe, which cannot be launched to start them, so they cannot be pinned yet.
    /// </summary>
    public bool CanPin =>
        !IsPinned && LaunchPath is { } path && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    public List<TrackedWindow> Windows { get; private set; } = [];

    public string? LaunchPath { get; private set; }

    public string? LaunchArguments { get; private set; }

    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }

    /// <summary>The app name for a pinned app that is not running; running apps show window previews instead.</summary>
    public string? ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(ShowInitial));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>The fallback initial letter is shown when there is no icon.</summary>
    public bool ShowInitial => _icon is null;

    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    public bool IsPinned { get => _isPinned; private set => SetProperty(ref _isPinned, value); }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }

    public bool HasMultipleWindows { get => _hasMultipleWindows; private set => SetProperty(ref _hasMultipleWindows, value); }

    /// <summary>Set by the view while the pointer is over the button.</summary>
    public bool IsHovered { get => _isHovered; set => SetProperty(ref _isHovered, value); }

    public bool CanLaunch => LaunchPath is not null;

    /// <summary>One of the app's windows asked for attention and has not been looked at yet.</summary>
    public bool IsFlashing { get => _isFlashing; private set => SetProperty(ref _isFlashing, value); }

    /// <summary>The app's overlay icon (unread count, status), drawn over the corner of its icon.</summary>
    public ImageSource? Badge
    {
        get => _badge;
        private set
        {
            if (SetProperty(ref _badge, value))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool HasBadge => _badge is not null;

    /// <summary>
    /// The button is new (an app just opened): the view plays its appear animation once, then clears this.
    /// False for the buttons the bar starts with, so a launch does not animate the whole row.
    /// </summary>
    public bool AnimateIn { get; set; }

    /// <summary>
    /// The app closed: the button stays in place while the view plays its disappear animation, then calls
    /// <see cref="CompleteRemoval"/>. Cleared again if the app comes back first.
    /// </summary>
    public bool IsLeaving { get => _isLeaving; internal set => SetProperty(ref _isLeaving, value); }

    /// <summary>Set by the view while an appear or disappear animation runs (no drag can start meanwhile).</summary>
    public bool IsAnimating { get; set; }

    /// <summary>The disappear animation finished: take the button off the bar.</summary>
    public void CompleteRemoval() => _owner.CompleteRemoval(this);

    internal void Update(PinnedApp? pin, List<TrackedWindow> windows, nint activeWindow, ImageSource? icon, ImageSource? badge = null)
    {
        Windows = windows;
        IsPinned = pin is not null;
        LaunchPath = pin?.Path ?? windows.FirstOrDefault()?.ProcessPath;
        LaunchArguments = pin?.Arguments;
        DisplayName = pin?.DisplayName
                      ?? (windows.FirstOrDefault()?.ProcessPath is { } p ? Path.GetFileNameWithoutExtension(p) : windows.FirstOrDefault()?.Title)
                      ?? "";
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(CanLaunch));
        _launchNewCommand.RaiseCanExecuteChanged();
        _togglePinCommand.RaiseCanExecuteChanged();
        Icon = icon;
        IsRunning = windows.Count > 0;
        HasMultipleWindows = windows.Count > 1;
        IsActive = activeWindow != 0 && windows.Any(w => w.Handle == activeWindow);
        IsFlashing = windows.Any(w => w.IsFlashing);
        Badge = badge;
        ToolTip = windows.Count == 0 ? DisplayName : null;
    }
}
