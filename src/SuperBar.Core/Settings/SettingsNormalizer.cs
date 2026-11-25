namespace SuperBar.Core.Settings;

/// <summary>
/// Repairs settings loaded from disk or edited by the user so the runtime can trust them:
/// clamps numeric ranges, removes dangling widget references and guarantees the core sections exist.
/// </summary>
public static class SettingsNormalizer
{
    public static SuperBarSettings Normalize(SuperBarSettings settings)
    {
        settings.Appearance ??= new AppearanceSettings();
        settings.Behavior ??= new BehaviorSettings();
        settings.Layout ??= new List<SectionPlacement>();
        settings.Widgets ??= new List<WidgetInstanceSettings>();
        settings.PinnedApps ??= new List<PinnedApp>();

        var a = settings.Appearance;
        a.Height = Clamp(a.Height, AppearanceSettings.MinHeight, AppearanceSettings.MaxHeight, 52);
        a.BackgroundOpacity = Clamp(a.BackgroundOpacity, 0, 1, 0.82);
        a.ItemCornerRadius = Clamp(a.ItemCornerRadius, 0, 24, 10);
        a.ItemSpacing = Clamp(a.ItemSpacing, 0, 24, 6);
        a.EdgePadding = Clamp(a.EdgePadding, 0, 48, 8);
        if (string.IsNullOrWhiteSpace(a.AccentColor))
        {
            a.AccentColor = "#5B8CFF";
        }

        MigrateClockSection(settings);

        // Widgets: unique ids, non-empty type.
        settings.Widgets = settings.Widgets
            .Where(w => w is not null && !string.IsNullOrWhiteSpace(w.TypeId) && !string.IsNullOrWhiteSpace(w.InstanceId))
            .GroupBy(w => w.InstanceId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        foreach (var w in settings.Widgets)
        {
            w.Options ??= new Dictionary<string, string>();
            if (w.Width is { } width && (double.IsNaN(width) || width <= 0))
            {
                w.Width = null;
            }
        }

        var widgetIds = settings.Widgets.Select(w => w.InstanceId).ToHashSet(StringComparer.Ordinal);

        // Layout: drop invalid/dangling entries and duplicate singleton sections.
        var seenSingletons = new HashSet<SectionKind>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var layout = new List<SectionPlacement>();
        foreach (var s in settings.Layout)
        {
            if (s is null || !Enum.IsDefined(s.Kind) || !Enum.IsDefined(s.Zone))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(s.Id) || !seenIds.Add(s.Id))
            {
                s.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(s.Id);
            }

            if (s.Kind == SectionKind.Widget)
            {
                if (s.WidgetInstanceId is null || !widgetIds.Contains(s.WidgetInstanceId))
                {
                    continue;
                }
            }
            else
            {
                s.WidgetInstanceId = null;
                if (!seenSingletons.Add(s.Kind))
                {
                    continue;
                }
            }

            layout.Add(s);
        }

        // A taskbar without Start or application icons is not a taskbar.
        if (!seenSingletons.Contains(SectionKind.Start))
        {
            layout.Insert(0, new SectionPlacement { Id = "start", Kind = SectionKind.Start, Zone = TaskbarZone.Left });
        }

        if (!seenSingletons.Contains(SectionKind.Applications))
        {
            var startIndex = layout.FindIndex(s => s.Kind == SectionKind.Start);
            layout.Insert(startIndex + 1, new SectionPlacement { Id = "apps", Kind = SectionKind.Applications, Zone = layout[startIndex].Zone });
        }

        settings.Layout = layout;

        settings.PinnedApps = settings.PinnedApps
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path))
            .ToList();

        settings.SchemaVersion = SuperBarSettings.CurrentSchemaVersion;
        return settings;
    }

    /// <summary>
    /// Schema 1 → 2: the clock section became a widget. The first legacy entry keeps its id, zone and position and
    /// now points at a clock widget instance (created when missing); any further legacy entries are dropped.
    /// </summary>
    private static void MigrateClockSection(SuperBarSettings settings)
    {
        var layout = settings.Layout;
        var migrated = false;
        for (var i = 0; i < layout.Count; i++)
        {
            var section = layout[i];
            if (section is not { Kind: SectionKind.Clock })
            {
                continue;
            }

            var instance = settings.Widgets.FirstOrDefault(w => w is not null && w.TypeId == DefaultSettings.ClockWidgetType);
            var alreadyPlaced = instance is not null
                && layout.Any(s => s is { Kind: SectionKind.Widget } && s.WidgetInstanceId == instance.InstanceId);
            if (migrated || alreadyPlaced)
            {
                layout.RemoveAt(i--);
                continue;
            }

            if (instance is null)
            {
                instance = new WidgetInstanceSettings { InstanceId = DefaultSettings.ClockWidgetInstanceId, TypeId = DefaultSettings.ClockWidgetType };
                settings.Widgets.Add(instance);
            }

            section.Kind = SectionKind.Widget;
            section.WidgetInstanceId = instance.InstanceId;
            migrated = true;
        }
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);
}
