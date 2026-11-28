using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Core.Layout;

/// <summary>What a sync changed.</summary>
public sealed record WindowsPinMergeResult(int Added, int Removed, int Updated, bool Reordered)
{
    public bool Changed => Added > 0 || Removed > 0 || Updated > 0 || Reordered;
}

/// <summary>
/// Reconciles SuperBar's pins with the Windows taskbar's (pure, so the rules are testable):
/// <list type="bullet">
/// <item>An app pinned in Windows is on the bar; unpinned in Windows, it leaves unless it was pinned in SuperBar too.</item>
/// <item>An app pinned in both is one button, never two.</item>
/// <item>Unpinned from the bar while pinned in Windows, it stays hidden until it is unpinned in Windows.</item>
/// <item>Order: the first sync takes Windows' order (SuperBar-only pins after). Afterwards the last place the user
/// reordered wins: reordering in Windows re-applies Windows' order to the Windows pins (SuperBar-only pins keep their
/// slots), and a drag in SuperBar holds until pins are reordered in Windows again. A newly pinned app goes right after
/// the pin before it in Windows.</item>
/// </list>
/// </summary>
public static class WindowsPinMerge
{
    /// <param name="pins">SuperBar's pins (hidden ones included); updated in place.</param>
    /// <param name="windowsPins">The Windows taskbar's pins, in order.</param>
    /// <param name="complete">
    /// False when the read missed entries: nothing is unpinned then, since a missing pin may just not be readable yet.
    /// </param>
    public static WindowsPinMergeResult Apply(List<PinnedApp> pins, IReadOnlyList<PinnedApp> windowsPins, bool complete)
    {
        var firstSync = !pins.Any(p => p.PinnedInWindows);
        var matched = new PinnedApp?[windowsPins.Count];
        var claimed = new HashSet<PinnedApp>(ReferenceEqualityComparer.Instance);

        // 1. Entries already pinned in Windows: same key first (exact), then same app (its id or target changed).
        for (var i = 0; i < windowsPins.Count; i++)
        {
            var key = AppOrdering.PinKey(windowsPins[i]);
            matched[i] = Claim(pins, claimed, p => p.PinnedInWindows && AppOrdering.PinKey(p) == key);
        }

        for (var i = 0; i < windowsPins.Count; i++)
        {
            matched[i] ??= Claim(pins, claimed, p => p.PinnedInWindows && PinMatching.SameApp(p, windowsPins[i]));
        }

        // 2. Apps pinned only in SuperBar so far become one entry with the Windows pin.
        for (var i = 0; i < windowsPins.Count; i++)
        {
            matched[i] ??= Claim(pins, claimed, p => !p.PinnedInWindows && PinMatching.SameApp(p, windowsPins[i]));
        }

        // Did the user reorder in Windows? Compare the order Windows had at the last sync with its order now.
        var previous = Enumerable.Range(0, windowsPins.Count)
            .Where(i => matched[i] is { PinnedInWindows: true, WindowsOrder: not null })
            .OrderBy(i => matched[i]!.WindowsOrder)
            .ToList();
        var reorderedInWindows = previous.Zip(previous.Skip(1)).Any(pair => pair.First > pair.Second);

        var added = 0;
        var removed = 0;
        var updated = 0;

        // 3. Unpinned in Windows.
        if (complete)
        {
            for (var i = pins.Count - 1; i >= 0; i--)
            {
                var pin = pins[i];
                if (!pin.PinnedInWindows || claimed.Contains(pin))
                {
                    continue;
                }

                if (pin.PinnedInSuperBar)
                {
                    pin.PinnedInWindows = false;
                    pin.HiddenFromBar = false;
                    pin.WindowsOrder = null;
                    updated++;
                }
                else
                {
                    pins.RemoveAt(i);
                    removed++;
                }
            }
        }

        // 4. Refresh what Windows knows better (name, target, shortcut), and add the new pins.
        for (var i = 0; i < windowsPins.Count; i++)
        {
            var source = windowsPins[i];
            if (matched[i] is { } pin)
            {
                if (CopyFrom(pin, source, i))
                {
                    updated++;
                }

                continue;
            }

            pin = new PinnedApp { PinnedInSuperBar = false };
            CopyFrom(pin, source, i);
            pins.Insert(InsertionIndex(pins, matched, i), pin);
            matched[i] = pin;
            added++;
        }

        // 5. Order.
        var reordered = false;
        if (firstSync && windowsPins.Count > 0)
        {
            var ordered = matched.Select(m => m!).Concat(pins.Where(p => !p.PinnedInWindows)).ToList();
            reordered = !ordered.SequenceEqual(pins);
            pins.Clear();
            pins.AddRange(ordered);
        }
        else if (reorderedInWindows)
        {
            var slots = Enumerable.Range(0, pins.Count).Where(i => pins[i].PinnedInWindows).ToList();
            var inWindowsOrder = slots.Select(i => pins[i]).OrderBy(p => p.WindowsOrder ?? int.MaxValue).ToList();
            for (var i = 0; i < slots.Count; i++)
            {
                if (!ReferenceEquals(pins[slots[i]], inWindowsOrder[i]))
                {
                    pins[slots[i]] = inWindowsOrder[i];
                    reordered = true;
                }
            }
        }

        return new WindowsPinMergeResult(added, removed, updated, reordered);
    }

    private static PinnedApp? Claim(List<PinnedApp> pins, HashSet<PinnedApp> claimed, Func<PinnedApp, bool> predicate)
    {
        var pin = pins.FirstOrDefault(p => !claimed.Contains(p) && predicate(p));
        if (pin is not null)
        {
            claimed.Add(pin);
        }

        return pin;
    }

    /// <summary>Right after the nearest earlier Windows pin; before the first Windows pin when it has none.</summary>
    private static int InsertionIndex(List<PinnedApp> pins, PinnedApp?[] matched, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            if (matched[j] is { } before && pins.IndexOf(before) is var at and >= 0)
            {
                return at + 1;
            }
        }

        var first = pins.FindIndex(p => p.PinnedInWindows);
        return first >= 0 ? first : 0;
    }

    /// <summary>Copies the Windows pin's details; returns whether anything changed.</summary>
    private static bool CopyFrom(PinnedApp pin, PinnedApp source, int windowsOrder)
    {
        var changed = !pin.PinnedInWindows
                      || pin.WindowsOrder != windowsOrder
                      || pin.DisplayName != source.DisplayName
                      || pin.Path != source.Path
                      || pin.Arguments != source.Arguments
                      || pin.WorkingDirectory != source.WorkingDirectory
                      || pin.AppUserModelId != source.AppUserModelId
                      || pin.ShortcutPath != source.ShortcutPath;

        pin.PinnedInWindows = true;
        pin.WindowsOrder = windowsOrder;
        pin.DisplayName = source.DisplayName;
        pin.Path = source.Path;
        pin.Arguments = source.Arguments;
        pin.WorkingDirectory = source.WorkingDirectory;
        pin.AppUserModelId = source.AppUserModelId;
        pin.ShortcutPath = source.ShortcutPath;
        return changed;
    }
}
