using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

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
    /// Re-orders <paramref name="pins"/> to follow <paramref name="keysInOrder"/> (pin keys, as the taskbar shows them).
    /// Pins the list does not mention (hidden ones included) keep their relative order after the ones it does.
    /// </summary>
    public static List<PinnedApp> ReorderPins(IReadOnlyList<PinnedApp> pins, IReadOnlyList<string> keysInOrder) =>
        Arrange(pins, PinKey, keysInOrder);

    /// <summary>The key of a path: lower-cased (paths are compared case-insensitively).</summary>
    public static string PinKey(string path) => path.ToLowerInvariant();

    /// <summary>
    /// The key of a pin, which is also its button's key on the bar: its explicit AppUserModelID when it has one (two
    /// browser profiles share an executable), otherwise its path.
    /// </summary>
    public static string PinKey(PinnedApp pin) =>
        string.IsNullOrEmpty(pin.AppUserModelId) ? PinKey(pin.Path) : "aumid:" + pin.AppUserModelId.ToLowerInvariant();

    /// <summary>
    /// Whether the pin is on the bar: pinned in SuperBar, or pinned in Windows (when Windows pins are shown) and not
    /// unpinned in SuperBar since.
    /// </summary>
    public static bool IsShown(PinnedApp pin, bool showWindowsPins) =>
        pin.PinnedInSuperBar || (showWindowsPins && pin.PinnedInWindows && !pin.HiddenFromBar);

    /// <summary>The pins on the bar, in order.</summary>
    public static List<PinnedApp> ShownPins(SuperBarSettings settings) =>
        settings.PinnedApps.Where(p => IsShown(p, settings.Behavior.SyncWindowsPins)).ToList();

    /// <summary>
    /// Pins the app at <paramref name="shownIndex"/> among the pins on the bar (clamped). An app already in the list
    /// (pinned in Windows, or unpinned from the bar) is pinned in SuperBar too and moved there instead of added twice.
    /// Returns false when the app was already on the bar.
    /// </summary>
    public static bool AddPin(List<PinnedApp> pins, PinnedApp pin, int shownIndex, bool showWindowsPins = true)
    {
        if (string.IsNullOrWhiteSpace(pin.Path))
        {
            return false;
        }

        var key = PinKey(pin);
        var existing = pins.FirstOrDefault(p => PinKey(p) == key) ?? pins.FirstOrDefault(p => PinMatching.SameApp(p, pin));
        if (existing is not null)
        {
            if (IsShown(existing, showWindowsPins))
            {
                return false;
            }

            pins.Remove(existing);
            existing.PinnedInSuperBar = true;
            existing.HiddenFromBar = false;
            pin = existing;
        }
        else
        {
            pin.PinnedInSuperBar = true;
        }

        // Translate the position among shown pins into a list index (hidden entries keep their places).
        var shown = 0;
        var index = pins.Count;
        for (var i = 0; i < pins.Count; i++)
        {
            if (!IsShown(pins[i], showWindowsPins))
            {
                continue;
            }

            if (shown == shownIndex)
            {
                index = i;
                break;
            }

            shown++;
        }

        pins.Insert(Math.Clamp(index, 0, pins.Count), pin);
        return true;
    }

    /// <summary>
    /// Unpins the app with that key from the bar. A pin that also lives on the Windows taskbar is hidden rather than
    /// removed, so the next sync does not bring it back. Returns how many entries changed.
    /// </summary>
    public static int RemovePin(List<PinnedApp> pins, string key)
    {
        var changed = 0;
        for (var i = pins.Count - 1; i >= 0; i--)
        {
            var pin = pins[i];
            if (PinKey(pin) != key)
            {
                continue;
            }

            changed++;
            if (pin.PinnedInWindows)
            {
                pin.PinnedInSuperBar = false;
                pin.HiddenFromBar = true;
            }
            else
            {
                pins.RemoveAt(i);
            }
        }

        return changed;
    }

    /// <summary>
    /// Brings a manual (dragged) button order in line with a new pin order, such as one that came from the Windows
    /// taskbar: the pins take the slots pins already had, in their new order, and pins it did not know go right after
    /// the pin before them. Running apps keep their places. An empty manual order stays empty.
    /// </summary>
    public static List<string> FollowPinOrder(IReadOnlyList<string> manualOrder, IReadOnlyList<string> pinKeys)
    {
        if (manualOrder.Count == 0)
        {
            return [];
        }

        var pinned = pinKeys.ToHashSet(StringComparer.Ordinal);
        var order = manualOrder.Distinct(StringComparer.Ordinal).ToList();
        var known = order.ToHashSet(StringComparer.Ordinal);

        var slots = new List<int>();
        for (var i = 0; i < order.Count; i++)
        {
            if (pinned.Contains(order[i]))
            {
                slots.Add(i);
            }
        }

        var present = pinKeys.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < slots.Count; i++)
        {
            order[slots[i]] = present[i];
        }

        for (var i = 0; i < pinKeys.Count; i++)
        {
            var key = pinKeys[i];
            if (order.Contains(key))
            {
                continue;
            }

            var previous = i > 0 ? order.IndexOf(pinKeys[i - 1]) : -1;
            if (previous >= 0)
            {
                order.Insert(previous + 1, key);
            }
            else
            {
                var firstPin = order.FindIndex(pinned.Contains);
                order.Insert(firstPin >= 0 ? firstPin : 0, key);
            }
        }

        return order;
    }
}
