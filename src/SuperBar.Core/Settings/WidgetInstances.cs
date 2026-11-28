namespace SuperBar.Core.Settings;

/// <summary>Adds and removes widget instances together with their place on the bar. Pure; used by the Control Center.</summary>
public static class WidgetInstances
{
    /// <summary>
    /// Adds an instance of <paramref name="typeId"/> and places it in <paramref name="zone"/>: at the end of the left
    /// and center zones, and in the right zone just before the system section (so it joins the other widgets rather
    /// than landing after the tray). Returns the new instance id, or null when <paramref name="allowMultiple"/> is
    /// false and the bar already has one.
    /// </summary>
    public static string? Add(SuperBarSettings settings, string typeId, TaskbarZone zone, bool allowMultiple)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        if (!allowMultiple && settings.Widgets.Any(w => w.TypeId == typeId))
        {
            return null;
        }

        var instanceId = NewInstanceId(settings, typeId);
        settings.Widgets.Add(new WidgetInstanceSettings { InstanceId = instanceId, TypeId = typeId });

        var placement = new SectionPlacement
        {
            Id = "widget-" + instanceId,
            Kind = SectionKind.Widget,
            Zone = zone,
            WidgetInstanceId = instanceId,
        };
        settings.Layout.Insert(InsertionIndex(settings.Layout, zone), placement);
        return instanceId;
    }

    /// <summary>Removes the instance and every layout entry that shows it. Returns false when there was no such instance.</summary>
    public static bool Remove(SuperBarSettings settings, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Layout.RemoveAll(p => p.Kind == SectionKind.Widget && p.WidgetInstanceId == instanceId);
        return settings.Widgets.RemoveAll(w => w.InstanceId == instanceId) > 0;
    }

    /// <summary>"superbar.system-resources" → "system-resources-1", "-2", … (the first number not in use).</summary>
    private static string NewInstanceId(SuperBarSettings settings, string typeId)
    {
        var dot = typeId.LastIndexOf('.');
        var stem = dot >= 0 && dot < typeId.Length - 1 ? typeId[(dot + 1)..] : typeId;
        var taken = settings.Widgets.Select(w => w.InstanceId).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static int InsertionIndex(List<SectionPlacement> layout, TaskbarZone zone)
    {
        if (zone == TaskbarZone.Right)
        {
            var tray = layout.FindIndex(p => p.Zone == TaskbarZone.Right && p.Kind == SectionKind.SystemTray);
            if (tray >= 0)
            {
                return tray;
            }
        }

        var last = layout.FindLastIndex(p => p.Zone == zone);
        return last >= 0 ? last + 1 : layout.Count;
    }
}
