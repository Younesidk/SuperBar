using SuperBar.Core.Layout;
using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class SectionOrderingTests
{
    private static SectionPlacement Widget(string id, TaskbarZone zone) =>
        new() { Id = "p-" + id, Kind = SectionKind.Widget, Zone = zone, WidgetInstanceId = id };

    private static List<SectionPlacement> Layout() =>
    [
        new() { Id = "start", Kind = SectionKind.Start, Zone = TaskbarZone.Left },
        Widget("a", TaskbarZone.Right),
        new() { Id = "apps", Kind = SectionKind.Applications, Zone = TaskbarZone.Left },
        new() { Id = "tray", Kind = SectionKind.SystemTray, Zone = TaskbarZone.Right },
        Widget("b", TaskbarZone.Right),
        Widget("c", TaskbarZone.Center),
    ];

    private static string[] Keys(List<SectionPlacement> layout) => layout.Select(SectionOrdering.Key).ToArray();

    [Fact]
    public void Keys_identify_sections()
    {
        Assert.Equal(
            ["start", "widget:a", "apps", "tray", "widget:b", "widget:c"],
            Keys(Layout()));
    }

    [Fact]
    public void Reordering_a_zone_leaves_the_other_zones_in_place()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.ReorderZone(layout, TaskbarZone.Right, ["widget:b", "widget:a", "tray"]));

        Assert.Equal(["start", "widget:b", "apps", "widget:a", "tray", "widget:c"], Keys(layout));
    }

    [Fact]
    public void Reordering_to_the_same_order_changes_nothing()
    {
        var layout = Layout();

        Assert.False(SectionOrdering.ReorderZone(layout, TaskbarZone.Right, ["widget:a", "tray", "widget:b"]));

        Assert.Equal(Keys(Layout()), Keys(layout));
    }

    [Fact]
    public void Sections_the_order_does_not_mention_stay_after_the_ones_it_does()
    {
        var layout = Layout();

        SectionOrdering.ReorderZone(layout, TaskbarZone.Right, ["widget:b"]);

        Assert.Equal(["start", "widget:b", "apps", "widget:a", "tray", "widget:c"], Keys(layout));
    }

    [Fact]
    public void Positions_without_a_move_are_the_packed_row() =>
        Assert.Equal([10, 116, 162], SectionOrdering.Positions([100, 40, 60], start: 10, spacing: 6, from: 1, target: 1));

    [Fact]
    public void Moving_an_item_right_packs_the_others_towards_the_start() =>
        Assert.Equal([122, 10, 56],SectionOrdering.Positions([100, 40, 60], start: 10, spacing: 6, from: 0, target: 2));

    [Fact]
    public void Moving_an_item_left_pushes_the_others_towards_the_end() =>
        Assert.Equal([76, 182, 10], SectionOrdering.Positions([100, 40, 60], start: 10, spacing: 6, from: 2, target: 0));

    [Theory]
    [InlineData(60, 0)]    // its own slot (center 60)
    [InlineData(82, 0)]    // still nearer to slot 0 than to slot 1 (center 106)
    [InlineData(84, 1)]
    [InlineData(140, 2)]   // slot 2 center is 172
    [InlineData(900, 2)]
    [InlineData(-50, 0)]
    public void Target_is_the_position_with_the_nearest_slot_center(double center, int expected) =>
        Assert.Equal(expected, SectionOrdering.TargetIndex([100, 40, 60], start: 10, spacing: 6, from: 0, center));
}
