using SuperBar.Core.Layout;
using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class AppOrderingTests
{
    private static List<string> Arrange(string[] natural, string[] manual) =>
        AppOrdering.Arrange(natural, s => s, manual);

    [Fact]
    public void Without_a_manual_order_the_natural_order_is_kept() =>
        Assert.Equal(["a", "b", "c"], Arrange(["a", "b", "c"], []));

    [Fact]
    public void Manual_order_wins() =>
        Assert.Equal(["c", "a", "b"], Arrange(["a", "b", "c"], ["c", "a", "b"]));

    [Fact]
    public void Unknown_items_go_to_the_end_in_natural_order() =>
        Assert.Equal(["b", "a", "x", "y"], Arrange(["x", "a", "y", "b"], ["b", "a"]));

    [Fact]
    public void Keys_that_are_gone_are_ignored() =>
        Assert.Equal(["b", "a"], Arrange(["a", "b"], ["gone", "b", "a"]));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(21, 1)]
    [InlineData(95, 2)]
    [InlineData(500, 3)]
    [InlineData(-50, 0)]
    public void Target_is_the_nearest_slot(double center, int expected) =>
        Assert.Equal(expected, AppOrdering.TargetIndex([0, 40, 80, 120], center));

    [Fact]
    public void Dragging_right_slides_the_items_in_between_left()
    {
        // item 1 dragged to slot 3
        Assert.Equal([0, 0, -1, -1, 0], Enumerable.Range(0, 5).Select(i => AppOrdering.ShiftDirection(i, 1, 3)));
    }

    [Fact]
    public void Dragging_left_slides_the_items_in_between_right()
    {
        // item 3 dragged to slot 0
        Assert.Equal([1, 1, 1, 0, 0], Enumerable.Range(0, 5).Select(i => AppOrdering.ShiftDirection(i, 3, 0)));
    }

    [Fact]
    public void Nothing_moves_when_dropped_in_place() =>
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(0, AppOrdering.ShiftDirection(i, 2, 2)));

    [Fact]
    public void Pins_follow_the_new_order_case_insensitively()
    {
        var pins = new List<PinnedApp>
        {
            new() { DisplayName = "Explorer", Path = @"C:\Windows\explorer.exe" },
            new() { DisplayName = "Edge", Path = @"C:\Edge\msedge.exe" },
            new() { DisplayName = "Code", Path = @"C:\Code\Code.exe" },
        };

        var result = AppOrdering.ReorderPins(pins, [@"c:\code\code.exe", @"c:\windows\explorer.exe"]);

        Assert.Equal(["Code", "Explorer", "Edge"], result.Select(p => p.DisplayName));
    }

    [Fact]
    public void Pinning_inserts_at_the_position_and_ignores_duplicates()
    {
        var pins = new List<PinnedApp>
        {
            new() { DisplayName = "A", Path = @"C:\a.exe" },
            new() { DisplayName = "B", Path = @"C:\b.exe" },
        };

        Assert.True(AppOrdering.AddPin(pins, new PinnedApp { DisplayName = "N", Path = @"C:\n.exe" }, 1));
        Assert.False(AppOrdering.AddPin(pins, new PinnedApp { DisplayName = "dup", Path = @"c:\N.EXE" }, 0));
        Assert.True(AppOrdering.AddPin(pins, new PinnedApp { DisplayName = "Z", Path = @"C:\z.exe" }, 99));

        Assert.Equal(["A", "N", "B", "Z"], pins.Select(p => p.DisplayName));
    }

    [Fact]
    public void Unpinning_removes_by_case_insensitive_path()
    {
        var pins = new List<PinnedApp> { new() { Path = @"C:\A.exe" }, new() { Path = @"C:\b.exe" } };

        Assert.Equal(1, AppOrdering.RemovePin(pins, AppOrdering.PinKey(@"c:\a.EXE")));
        Assert.Equal([@"C:\b.exe"], pins.Select(p => p.Path));
    }
}
