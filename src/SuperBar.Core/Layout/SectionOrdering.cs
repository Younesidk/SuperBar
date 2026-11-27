using SuperBar.Core.Settings;

namespace SuperBar.Core.Layout;

/// <summary>
/// Pure ordering and drag-reorder math for the bar's sections (widgets are dragged; see the runtime's
/// WidgetDragController). Unlike application buttons, sections have different widths, so slots are computed from the
/// widths rather than swapped.
/// </summary>
public static class SectionOrdering
{
    public const string StartKey = "start";
    public const string AppsKey = "apps";
    public const string TrayKey = "tray";

    /// <summary>The key of a widget instance's section; the runtime uses it as the section's id.</summary>
    public static string WidgetKey(string instanceId) => "widget:" + instanceId;

    /// <summary>The key of the section a layout entry places (the same value as the runtime section's id).</summary>
    public static string Key(SectionPlacement placement) => placement.Kind switch
    {
        SectionKind.Start => StartKey,
        SectionKind.Applications => AppsKey,
        SectionKind.SystemTray => TrayKey,
        SectionKind.Widget => WidgetKey(placement.WidgetInstanceId ?? ""),
        _ => placement.Id,
    };

    /// <summary>
    /// Re-orders the entries of <paramref name="zone"/> in <paramref name="layout"/> to follow
    /// <paramref name="keysInOrder"/>. Entries of other zones keep their places, and entries the list does not mention
    /// keep their relative order after the ones it does. Returns false when nothing changed.
    /// </summary>
    public static bool ReorderZone(List<SectionPlacement> layout, TaskbarZone zone, IReadOnlyList<string> keysInOrder)
    {
        var indexes = Enumerable.Range(0, layout.Count).Where(i => layout[i].Zone == zone).ToList();
        var current = indexes.Select(i => layout[i]).ToList();
        var arranged = AppOrdering.Arrange(current, Key, keysInOrder);
        if (arranged.SequenceEqual(current))
        {
            return false;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            layout[indexes[i]] = arranged[i];
        }

        return true;
    }

    /// <summary>
    /// Where each item of a tightly packed row sits (left edge) once the item at <paramref name="from"/> has been moved
    /// to position <paramref name="target"/>. The result is indexed like <paramref name="widths"/> (the original order).
    /// </summary>
    public static double[] Positions(IReadOnlyList<double> widths, double start, double spacing, int from, int target)
    {
        var order = Enumerable.Range(0, widths.Count).ToList();
        order.RemoveAt(from);
        order.Insert(Math.Clamp(target, 0, order.Count), from);

        var positions = new double[widths.Count];
        var x = start;
        foreach (var index in order)
        {
            positions[index] = x;
            x += widths[index] + spacing;
        }

        return positions;
    }

    /// <summary>
    /// The position the dragged item should take: the one whose slot center is nearest to
    /// <paramref name="draggedCenter"/>. Ties keep the earlier position.
    /// </summary>
    public static int TargetIndex(IReadOnlyList<double> widths, double start, double spacing, int from, double draggedCenter)
    {
        var best = 0;
        var bestDistance = double.PositiveInfinity;
        for (var target = 0; target < widths.Count; target++)
        {
            var center = Positions(widths, start, spacing, from, target)[from] + widths[from] / 2;
            var distance = Math.Abs(center - draggedCenter);
            if (distance < bestDistance)
            {
                best = target;
                bestDistance = distance;
            }
        }

        return best;
    }
}
