using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Services;
using SuperBar.Widgets;

namespace SuperBar.Runtime.ViewModels;

/// <summary>
/// The single taskbar surface: three zones of modular sections built from <see cref="SuperBarSettings.Layout"/>.
/// Widget instances are hosted (not windowed) and their lifecycles follow the bar's visibility.
/// </summary>
public sealed class TaskbarViewModel : ObservableObject, IDisposable
{
    private readonly IWidgetRegistry _widgets;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly StartSectionViewModel _start;
    private readonly SystemTraySectionViewModel _tray;
    private readonly ClockSectionViewModel _clock;
    private readonly Dictionary<string, WidgetSectionViewModel> _widgetSections = new(StringComparer.Ordinal);
    private bool _active;
    private double _itemSpacing = 6;
    private Thickness _surfacePadding = new(8, 0);

    public TaskbarViewModel(
        IShellCommands shell,
        IWindowTracker tracker,
        IWindowSwitcher switcher,
        IAppLauncher launcher,
        IconCache icons,
        IWidgetRegistry widgets,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        Action openControlCenter,
        Action exit)
    {
        _widgets = widgets;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TaskbarViewModel>();
        _start = new StartSectionViewModel("start", shell);
        Apps = new AppsSectionViewModel("apps", tracker, switcher, launcher, icons, loggerFactory.CreateLogger<AppsSectionViewModel>());
        _tray = new SystemTraySectionViewModel("tray", shell);
        _clock = new ClockSectionViewModel("clock", shell);

        OpenControlCenterCommand = new RelayCommand(openControlCenter);
        OpenTaskManagerCommand = new RelayCommand(shell.OpenTaskManager);
        ExitCommand = new RelayCommand(exit);
    }

    public AppsSectionViewModel Apps { get; }

    public ObservableCollection<SectionViewModel> LeftSections { get; } = new();
    public ObservableCollection<SectionViewModel> CenterSections { get; } = new();
    public ObservableCollection<SectionViewModel> RightSections { get; } = new();

    public ICommand OpenControlCenterCommand { get; }
    public ICommand OpenTaskManagerCommand { get; }
    public ICommand ExitCommand { get; }

    public double ItemSpacing { get => _itemSpacing; private set => SetProperty(ref _itemSpacing, value); }

    public Thickness SurfacePadding { get => _surfacePadding; private set => SetProperty(ref _surfacePadding, value); }

    public void Apply(SuperBarSettings settings)
    {
        ItemSpacing = settings.Appearance.ItemSpacing;
        SurfacePadding = new Thickness(settings.Appearance.EdgePadding, 0);
        Apps.SetPins(settings.PinnedApps);
        SyncWidgets(settings);
        SyncZone(LeftSections, settings, TaskbarZone.Left);
        SyncZone(CenterSections, settings, TaskbarZone.Center);
        SyncZone(RightSections, settings, TaskbarZone.Right);
    }

    public void Activate()
    {
        _active = true;
        foreach (var s in AllSections())
        {
            s.Activate();
        }
    }

    public void Deactivate()
    {
        _active = false;
        foreach (var s in AllSections())
        {
            s.Deactivate();
        }
    }

    private IEnumerable<SectionViewModel> AllSections() => LeftSections.Concat(CenterSections).Concat(RightSections);

    private void SyncWidgets(SuperBarSettings settings)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in settings.Widgets.Where(w => w.Enabled))
        {
            if (!settings.Layout.Any(s => s.Kind == SectionKind.Widget && s.WidgetInstanceId == instance.InstanceId))
            {
                continue;
            }

            if (!_widgets.TryGet(instance.TypeId, out var descriptor))
            {
                _logger.LogWarning("Unknown widget type {TypeId} for instance {InstanceId}", instance.TypeId, instance.InstanceId);
                continue;
            }

            wanted.Add(instance.InstanceId);
            if (_widgetSections.TryGetValue(instance.InstanceId, out var existing))
            {
                existing.Host.Apply(instance);
                existing.RefreshWidth();
            }
            else
            {
                var host = new WidgetHost(descriptor, instance, _services, _loggerFactory);
                _widgetSections[instance.InstanceId] = new WidgetSectionViewModel("widget:" + instance.InstanceId, host);
            }
        }

        foreach (var id in _widgetSections.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            var section = _widgetSections[id];
            _widgetSections.Remove(id);
            RemoveFromZones(section);
            section.Host.Dispose();
        }
    }

    private void RemoveFromZones(SectionViewModel section)
    {
        LeftSections.Remove(section);
        CenterSections.Remove(section);
        RightSections.Remove(section);
    }

    private void SyncZone(ObservableCollection<SectionViewModel> zone, SuperBarSettings settings, TaskbarZone which)
    {
        var desired = settings.Layout
            .Where(s => s.Zone == which)
            .Select(Resolve)
            .OfType<SectionViewModel>()
            .ToList();

        if (desired.SequenceEqual(zone))
        {
            return;
        }

        // Reset rather than move: hosted widget views must be detached before being re-parented.
        foreach (var removed in zone.Except(desired).ToList())
        {
            removed.Deactivate();
        }

        zone.Clear();
        foreach (var s in desired)
        {
            // A section moved from another zone must leave it first.
            RemoveFromZones(s);
            zone.Add(s);
            if (_active)
            {
                s.Activate();
            }
        }
    }

    private SectionViewModel? Resolve(SectionPlacement placement) => placement.Kind switch
    {
        SectionKind.Start => _start,
        SectionKind.Applications => Apps,
        SectionKind.SystemTray => _tray,
        SectionKind.Clock => _clock,
        SectionKind.Widget when placement.WidgetInstanceId is { } id && _widgetSections.TryGetValue(id, out var w) => w,
        _ => null,
    };

    public void Dispose()
    {
        Deactivate();
        foreach (var w in _widgetSections.Values)
        {
            w.Host.Dispose();
        }

        _widgetSections.Clear();
    }
}
