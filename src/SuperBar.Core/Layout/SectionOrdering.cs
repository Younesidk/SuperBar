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
    /// Moves the entry one step along the bar: earlier (direction &lt; 0) or later (direction &gt; 0). Within its zone it
    /// trades places with its neighbor. At the edge of its zone it crosses into the adjacent zone, entering at the near
    /// end (moving later from the end of Left makes it the first item of Center). Returns false when nothing changed
    /// (unknown id, first item of Left moving earlier, last item of Right moving later).
    /// </summary>
    /// <remarks>
    /// The bar is read as one line, Left zone then Center then Right. A zone's left-to-right order is the order of its
    /// entries in the flat list; the relative order of entries of different zones means nothing and is left alone.
    /// </remarks>
    public static bool Shift(List<SectionPlacement> layout, string placementId, int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (direction == 0)
        {
            return false;
        }

        var index = layout.FindIndex(p => p.Id == placementId);
        if (index < 0)
        {
            return false;
        }

        var entry = layout[index];
        var zoneIndexes = ZoneIndexes(layout, entry.Zone);
        var position = zoneIndexes.IndexOf(index);
        var later = direction > 0;
        var neighbor = later ? position + 1 : position - 1;

        if (neighbor >= 0 && neighbor < zoneIndexes.Count)
        {
            // Within the zone: trade slots with the neighbor; other zones' entries keep theirs.
            var other = zoneIndexes[neighbor];
            (layout[index], layout[other]) = (layout[other], layout[index]);
            return true;
        }

        if (AdjacentZone(entry.Zone, later) is not { } target)
        {
            return false;
        }

        // Crossing into the adjacent zone, entering at its near end.
        layout.RemoveAt(index);
        entry.Zone = target;
        var targetIndexes = ZoneIndexes(layout, target);
        if (targetIndexes.Count == 0)
        {
            layout.Add(entry);
        }
        else if (later)
        {
            layout.Insert(targetIndexes[0], entry);
        }
        else
        {
            layout.Insert(targetIndexes[^1] + 1, entry);
        }

        return true;
    }

    /// <summary>Puts the entry at the end of <paramref name="zone"/>. Returns false when it is already in that zone or unknown.</summary>
    public static bool MoveToZone(List<SectionPlacement> layout, string placementId, TaskbarZone zone)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var index = layout.FindIndex(p => p.Id == placementId);
        if (index < 0 || layout[index].Zone == zone)
        {
            return false;
        }

        var entry = layout[index];
        layout.RemoveAt(index);
        entry.Zone = zone;
        var targetIndexes = ZoneIndexes(layout, zone);
        if (targetIndexes.Count == 0)
        {
            layout.Add(entry);
        }
        else
        {
            layout.Insert(targetIndexes[^1] + 1, entry);
        }

        return true;
    }

    /// <summary>Whether <see cref="Shift"/> would change anything.</summary>
    public static bool CanShift(IReadOnlyList<SectionPlacement> layout, string placementId, int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (direction == 0)
        {
            return false;
        }

        if (layout.FirstOrDefault(p => p.Id == placementId) is not { } entry)
        {
            return false;
        }

        var zone = layout.Where(p => p.Zone == entry.Zone).ToList();
        var position = zone.IndexOf(entry);
        var later = direction > 0;
        var neighbor = later ? position + 1 : position - 1;
        return (neighbor >= 0 && neighbor < zone.Count) || AdjacentZone(entry.Zone, later) is not null;
    }

    private static List<int> ZoneIndexes(List<SectionPlacement> layout, TaskbarZone zone) =>
        Enumerable.Range(0, layout.Count).Where(i => layout[i].Zone == zone).ToList();

    // Zone order along the bar is the enum order: Left, Center, Right.
    private static TaskbarZone? AdjacentZone(TaskbarZone zone, bool later) => (zone, later) switch
    {
        (TaskbarZone.Left, true) => TaskbarZone.Center,
        (TaskbarZone.Center, true) => TaskbarZone.Right,
        (TaskbarZone.Center, false) => TaskbarZone.Left,
        (TaskbarZone.Right, false) => TaskbarZone.Center,
        _ => null,
    };

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
