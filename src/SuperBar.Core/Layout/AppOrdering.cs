using SuperBar.Core.Settings;

namespace SuperBar.Core.Layout;

/// <summary>Pure ordering and drag-reorder math for the taskbar's application buttons.</summary>
public static class AppOrdering
{
    /// <summary>
    /// Orders <paramref name="items"/> by the user's manual order (<paramref name="manualOrder"/>, a list of keys from the
    /// last drag). Items the manual order does not know (newly pinned or newly started apps) go to the end, keeping their
    /// natural relative order. With no manual order the natural order is returned unchanged.
    /// </summary>
    public static List<T> Arrange<T>(IReadOnlyList<T> items, Func<T, string> key, IReadOnlyList<string> manualOrder)
    {
        if (manualOrder.Count == 0)
        {
            return items.ToList();
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < manualOrder.Count; i++)
        {
            rank.TryAdd(manualOrder[i], i);
        }

        return items
            .Select((item, index) => (item, index, rank: rank.TryGetValue(key(item), out var r) ? r : int.MaxValue))
            .OrderBy(x => x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>The slot whose center is nearest to <paramref name="draggedCenter"/>.</summary>
    public static int TargetIndex(IReadOnlyList<double> slotCenters, double draggedCenter)
    {
        var best = 0;
        for (var i = 1; i < slotCenters.Count; i++)
        {
            if (Math.Abs(slotCenters[i] - draggedCenter) < Math.Abs(slotCenters[best] - draggedCenter))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// While an item is dragged from <paramref name="from"/> towards <paramref name="target"/>, the items in between
    /// make room: -1 means "slide one slot to the left", +1 "one slot to the right", 0 "stay".
    /// </summary>
    public static int ShiftDirection(int index, int from, int target)
    {
        if (index == from)
        {
            return 0;
        }

        if (from < target && index > from && index <= target)
        {
            return -1;
        }

        if (target < from && index >= target && index < from)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Re-orders <paramref name="pins"/> to follow <paramref name="keysInOrder"/> (lower-cased paths, as the taskbar
    /// shows them). Pins the list does not mention keep their relative order after the ones it does.
    /// </summary>
    public static List<PinnedApp> ReorderPins(IReadOnlyList<PinnedApp> pins, IReadOnlyList<string> keysInOrder) =>
        Arrange(pins, p => p.Path.ToLowerInvariant(), keysInOrder);

    /// <summary>The key of a pin: its path, lower-cased (paths are compared case-insensitively).</summary>
    public static string PinKey(string path) => path.ToLowerInvariant();

    /// <summary>Inserts <paramref name="pin"/> at <paramref name="index"/> (clamped), unless that path is already pinned.</summary>
    public static bool AddPin(List<PinnedApp> pins, PinnedApp pin, int index)
    {
        var key = PinKey(pin.Path);
        if (string.IsNullOrWhiteSpace(pin.Path) || pins.Any(p => PinKey(p.Path) == key))
        {
            return false;
        }

        pins.Insert(Math.Clamp(index, 0, pins.Count), pin);
        return true;
    }

    /// <summary>Removes every pin of the app with that key (lower-cased path). Returns how many were removed.</summary>
    public static int RemovePin(List<PinnedApp> pins, string key) => pins.RemoveAll(p => PinKey(p.Path) == key);
}
