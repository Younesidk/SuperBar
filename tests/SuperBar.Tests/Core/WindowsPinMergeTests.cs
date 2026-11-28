using SuperBar.Core.Layout;
using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class WindowsPinMergeTests
{
    private static PinnedApp Windows(string name, string? appId = null, string? path = null) =>
        new() { DisplayName = name, Path = path ?? $@"C:\Apps\{name}.exe", AppUserModelId = appId, ShortcutPath = $@"C:\Pinned\{name}.lnk" };

    private static PinnedApp Local(string name, string? path = null) =>
        new() { DisplayName = name, Path = path ?? $@"C:\Apps\{name}.exe" };

    private static List<PinnedApp> W(params string[] names) => names.Select(n => Windows(n)).ToList();

    private static string[] Shown(List<PinnedApp> pins) =>
        pins.Where(p => AppOrdering.IsShown(p, showWindowsPins: true)).Select(p => p.DisplayName).ToArray();

    [Fact]
    public void First_sync_takes_the_windows_order_and_keeps_superbar_pins_after()
    {
        var pins = new List<PinnedApp> { Local("Tools"), Local("B") };

        var result = WindowsPinMerge.Apply(pins, W("A", "B", "C"), complete: true);

        Assert.Equal(["A", "B", "C", "Tools"], Shown(pins));
        Assert.Equal(2, result.Added);
        Assert.True(result.Reordered);
    }

    [Fact]
    public void An_app_pinned_in_both_places_is_one_entry_that_survives_a_windows_unpin()
    {
        var pins = new List<PinnedApp> { Local("B") };
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);

        var b = Assert.Single(pins, p => p.DisplayName == "B");
        Assert.True(b.PinnedInWindows);
        Assert.True(b.PinnedInSuperBar);

        WindowsPinMerge.Apply(pins, W("A"), complete: true);

        Assert.Equal(["A", "B"], Shown(pins));
        Assert.False(pins.Single(p => p.DisplayName == "B").PinnedInWindows);
    }

    [Fact]
    public void Unpinned_in_windows_leaves_the_bar()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B", "C"), complete: true);

        var result = WindowsPinMerge.Apply(pins, W("A", "C"), complete: true);

        Assert.Equal(["A", "C"], Shown(pins));
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void A_partial_read_never_unpins()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B", "C"), complete: true);

        var result = WindowsPinMerge.Apply(pins, W("A", "C"), complete: false);

        Assert.Equal(["A", "B", "C"], Shown(pins));
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void A_new_windows_pin_goes_after_the_pin_before_it_in_windows()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);
        pins.Insert(1, Local("Mine")); // A, Mine, B

        WindowsPinMerge.Apply(pins, W("A", "New", "B"), complete: true);

        Assert.Equal(["A", "New", "Mine", "B"], Shown(pins));
    }

    [Fact]
    public void A_drag_in_superbar_holds_until_pins_are_reordered_in_windows()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B", "C"), complete: true);

        // Dragged in SuperBar: C first.
        pins = AppOrdering.ReorderPins(pins, ["c:\\apps\\c.exe", "c:\\apps\\a.exe", "c:\\apps\\b.exe"]);
        var unchanged = WindowsPinMerge.Apply(pins, W("A", "B", "C"), complete: true);
        Assert.Equal(["C", "A", "B"], Shown(pins));
        Assert.False(unchanged.Changed);

        // Reordered in Windows: Windows' order wins.
        var reordered = WindowsPinMerge.Apply(pins, W("B", "A", "C"), complete: true);
        Assert.Equal(["B", "A", "C"], Shown(pins));
        Assert.True(reordered.Reordered);
    }

    [Fact]
    public void A_windows_reorder_keeps_superbar_only_pins_in_their_slots()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);
        pins.Insert(1, Local("Mine")); // A, Mine, B

        WindowsPinMerge.Apply(pins, W("B", "A"), complete: true);

        Assert.Equal(["B", "Mine", "A"], Shown(pins));
    }

    [Fact]
    public void Unpinned_in_superbar_stays_hidden_until_unpinned_in_windows_then_comes_back_when_pinned_again()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);

        AppOrdering.RemovePin(pins, AppOrdering.PinKey(pins.Single(p => p.DisplayName == "B")));
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);
        Assert.Equal(["A"], Shown(pins));

        WindowsPinMerge.Apply(pins, W("A"), complete: true);
        Assert.DoesNotContain(pins, p => p.DisplayName == "B");

        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);
        Assert.Equal(["A", "B"], Shown(pins));
    }

    [Fact]
    public void Pinning_a_hidden_windows_pin_in_superbar_shows_it_again_without_a_duplicate()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A", "B"), complete: true);
        AppOrdering.RemovePin(pins, AppOrdering.PinKey(pins[1]));

        Assert.True(AppOrdering.AddPin(pins, Local("B"), 1));

        Assert.Equal(["A", "B"], Shown(pins));
        Assert.Equal(2, pins.Count);
    }

    [Fact]
    public void Details_follow_windows_and_an_unchanged_read_changes_nothing()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins, W("A"), complete: true);
        var id = pins[0].Id;

        var renamed = Windows("A");
        renamed.DisplayName = "A (renamed)";
        renamed.Arguments = "--profile 2";
        var result = WindowsPinMerge.Apply(pins, [renamed], complete: true);

        Assert.Equal(1, result.Updated);
        Assert.Equal(id, pins[0].Id);
        Assert.Equal("--profile 2", pins[0].Arguments);
        Assert.False(WindowsPinMerge.Apply(pins, [renamed], complete: true).Changed);
    }

    [Fact]
    public void Browser_profiles_sharing_an_executable_stay_separate_pins()
    {
        var pins = new List<PinnedApp>();
        WindowsPinMerge.Apply(pins,
        [
            Windows("Work", "Chrome.Work", @"C:\Chrome\chrome.exe"),
            Windows("Home", "Chrome.Home", @"C:\Chrome\chrome.exe"),
        ], complete: true);

        Assert.Equal(["Work", "Home"], Shown(pins));
    }

    [Fact]
    public void Turning_windows_pins_off_shows_only_superbar_pins()
    {
        var pins = new List<PinnedApp> { Local("Mine") };
        WindowsPinMerge.Apply(pins, W("A"), complete: true);

        Assert.Equal(["Mine"], pins.Where(p => AppOrdering.IsShown(p, showWindowsPins: false)).Select(p => p.DisplayName));
    }

    [Fact]
    public void The_normalizer_drops_pins_nothing_pins_and_unhides_superbar_pins()
    {
        var settings = new SuperBarSettings
        {
            PinnedApps =
            {
                new PinnedApp { Path = @"C:\a.exe", PinnedInSuperBar = false, PinnedInWindows = false },
                new PinnedApp { Path = @"C:\b.exe", PinnedInSuperBar = true, HiddenFromBar = true },
            },
        };

        SettingsNormalizer.Normalize(settings);

        var pin = Assert.Single(settings.PinnedApps);
        Assert.False(pin.HiddenFromBar);
    }
}
