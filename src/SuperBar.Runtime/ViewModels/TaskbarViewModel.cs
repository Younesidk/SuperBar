using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Layout;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Services;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.Widgets;
using SuperBar.Widgets;

namespace SuperBar.Runtime.ViewModels;

/// <summary>
/// The single taskbar surface: three zones of modular sections built from <see cref="SuperBarSettings.Layout"/>.
/// Widget instances are hosted (not windowed) and their lifecycles follow the bar's visibility.
/// Visual metrics (spacing, padding, item size, corners) live in <see cref="Theming.TaskbarTheme"/>.
/// </summary>
public sealed class TaskbarViewModel : ObservableObject, IDisposable
{
    private readonly IWidgetRegistry _widgets;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WidgetViewResolver _widgetViews;
    private readonly TaskbarTheme _theme;
    private readonly ILogger _logger;
    private readonly StartSectionViewModel _start;
    private readonly SystemTraySectionViewModel _tray;
    private readonly Dictionary<string, WidgetSectionViewModel> _widgetSections = new(StringComparer.Ordinal);
    private bool _active;

    public TaskbarViewModel(
        IShellCommands shell,
        IWindowTracker tracker,
        IWindowSwitcher switcher,
        IAppLauncher launcher,
        IconCache icons,
        IWidgetRegistry widgets,
        WidgetViewResolver widgetViews,
        TaskbarTheme theme,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        Action openControlCenter,
        Action exit)
    {
        _widgets = widgets;
        _services = services;
        _loggerFactory = loggerFactory;
        _widgetViews = widgetViews;
        _theme = theme;
        _logger = loggerFactory.CreateLogger<TaskbarViewModel>();
        _start = new StartSectionViewModel(SectionOrdering.StartKey, shell);
        Apps = new AppsSectionViewModel(SectionOrdering.AppsKey, tracker, switcher, launcher, icons, loggerFactory.CreateLogger<AppsSectionViewModel>());
        _tray = new SystemTraySectionViewModel(SectionOrdering.TrayKey, shell);

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

    /// <summary>The on-screen element of a widget instance's slot, or null when it is not shown.</summary>
    public Microsoft.UI.Xaml.FrameworkElement? FindWidgetElement(string instanceId) =>
        _widgetSections.TryGetValue(instanceId, out var section) && section.CurrentElement is { IsLoaded: true } element ? element : null;

    /// <summary>
    /// The user dragged a section to a new place in its zone: the zone and its section ids in the new order. The
    /// runtime persists it in the layout.
    /// </summary>
    public event Action<TaskbarZone, IReadOnlyList<string>>? SectionOrderChanged;

    /// <summary>
    /// Moves <paramref name="moved"/> next to <paramref name="neighbor"/> (just after it, or just before) within their
    /// zone, in place, so every other section keeps its item container. Returns false when they do not share a zone.
    /// </summary>
    public bool MoveSection(SectionViewModel moved, SectionViewModel neighbor, bool after)
    {
        foreach (var (zone, sections) in new[] { (TaskbarZone.Left, LeftSections), (TaskbarZone.Center, CenterSections), (TaskbarZone.Right, RightSections) })
        {
            var from = sections.IndexOf(moved);
            var anchor = sections.IndexOf(neighbor);
            if (from < 0 || anchor < 0 || from == anchor)
            {
                continue;
            }

            // Index of the neighbor once the moved section is taken out.
            var to = (anchor > from ? anchor - 1 : anchor) + (after ? 1 : 0);
            if (to != from)
            {
                sections.Move(from, to);
                SectionOrderChanged?.Invoke(zone, sections.Select(s => s.Id).ToList());
            }

            return true;
        }

        return false;
    }

    public void Apply(SuperBarSettings settings)
    {
        Apps.SetPins(AppOrdering.ShownPins(settings));
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
                _widgetSections[instance.InstanceId] = new WidgetSectionViewModel(SectionOrdering.WidgetKey(instance.InstanceId), host, _widgetViews, _theme);
            }
        }

        foreach (var id in _widgetSections.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            var section = _widgetSections[id];
            _widgetSections.Remove(id);
            RemoveFromZones(section);
            section.Dispose();
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

        // Sections that leave this zone (removed, or moved to another zone) stop first.
        foreach (var removed in zone.Except(desired).ToList())
        {
            removed.Deactivate();
            zone.Remove(removed);
        }

        // Edit the collection in place rather than resetting it: a section that stays keeps its item container,
        // so its hosted widget view is not re-parented when another widget is added, removed or toggled.
        for (var i = 0; i < desired.Count; i++)
        {
            var s = desired[i];
            var current = zone.IndexOf(s);
            if (current == i)
            {
                continue;
            }

            if (current >= 0)
            {
                zone.Move(current, i);
                continue;
            }

            // A section moved from another zone must leave it first.
            RemoveFromZones(s);
            zone.Insert(i, s);
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
        SectionKind.Widget when placement.WidgetInstanceId is { } id && _widgetSections.TryGetValue(id, out var w) => w,
        _ => null,
    };

    public void Dispose()
    {
        Deactivate();
        foreach (var w in _widgetSections.Values)
        {
            w.Dispose();
        }

        _widgetSections.Clear();
    }
}
