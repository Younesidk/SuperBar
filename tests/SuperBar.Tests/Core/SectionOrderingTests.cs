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

    private static string[] Zone(List<SectionPlacement> layout, TaskbarZone zone) =>
        layout.Where(p => p.Zone == zone).Select(SectionOrdering.Key).ToArray();

    [Fact]
    public void Shifting_within_a_zone_leaves_the_other_zones_in_their_slots()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.Shift(layout, "apps", -1));

        Assert.Equal(["apps", "widget:a", "start", "tray", "widget:b", "widget:c"], Keys(layout));
    }

    [Fact]
    public void Shifting_later_within_a_zone_trades_places_with_the_next_entry()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.Shift(layout, "p-a", +1));

        Assert.Equal(["tray", "widget:a", "widget:b"], Zone(layout, TaskbarZone.Right));
        Assert.Equal(["start", "apps"], Zone(layout, TaskbarZone.Left));
    }

    [Fact]
    public void Shifting_later_from_the_end_of_left_enters_center_first()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.Shift(layout, "apps", +1));

        Assert.Equal(TaskbarZone.Center, layout.Single(p => p.Id == "apps").Zone);
        Assert.Equal(["start"], Zone(layout, TaskbarZone.Left));
        Assert.Equal(["apps", "widget:c"], Zone(layout, TaskbarZone.Center));
    }

    [Fact]
    public void Shifting_earlier_from_the_start_of_center_enters_left_last()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.Shift(layout, "p-c", -1));

        Assert.Equal(TaskbarZone.Left, layout.Single(p => p.Id == "p-c").Zone);
        Assert.Equal(["start", "apps", "widget:c"], Zone(layout, TaskbarZone.Left));
        Assert.Empty(Zone(layout, TaskbarZone.Center));
    }

    [Fact]
    public void Shifting_later_from_the_end_of_center_enters_right_first()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.Shift(layout, "p-c", +1));

        Assert.Equal(["widget:c", "widget:a", "tray", "widget:b"], Zone(layout, TaskbarZone.Right));
    }

    [Fact]
    public void Shifting_into_an_empty_zone_makes_it_the_only_entry()
    {
        var layout = Layout();
        layout.RemoveAll(p => p.Zone == TaskbarZone.Center);

        Assert.True(SectionOrdering.Shift(layout, "apps", +1));

        Assert.Equal(["apps"], Zone(layout, TaskbarZone.Center));
        Assert.Equal(["start"], Zone(layout, TaskbarZone.Left));
    }

    [Theory]
    [InlineData("start", -1)]   // first of Left, moving earlier
    [InlineData("p-b", +1)]     // last of Right, moving later
    [InlineData("missing", +1)]
    [InlineData("apps", 0)]
    public void Shifting_past_the_ends_of_the_bar_changes_nothing(string id, int direction)
    {
        var layout = Layout();

        Assert.False(SectionOrdering.Shift(layout, id, direction));

        Assert.Equal(Keys(Layout()), Keys(layout));
        Assert.Equal(Layout().Select(p => p.Zone), layout.Select(p => p.Zone));
    }

    [Fact]
    public void Moving_to_a_zone_appends_the_entry_to_it()
    {
        var layout = Layout();

        Assert.True(SectionOrdering.MoveToZone(layout, "start", TaskbarZone.Right));

        Assert.Equal(["widget:a", "tray", "widget:b", "start"], Zone(layout, TaskbarZone.Right));
        Assert.Equal(["apps"], Zone(layout, TaskbarZone.Left));
    }

    [Fact]
    public void Moving_to_an_empty_zone_makes_it_the_only_entry()
    {
        var layout = Layout();
        layout.RemoveAll(p => p.Zone == TaskbarZone.Center);

        Assert.True(SectionOrdering.MoveToZone(layout, "tray", TaskbarZone.Center));

        Assert.Equal(["tray"], Zone(layout, TaskbarZone.Center));
    }

    [Theory]
    [InlineData("start", TaskbarZone.Left)]
    [InlineData("missing", TaskbarZone.Center)]
    public void Moving_to_the_current_zone_or_an_unknown_entry_changes_nothing(string id, TaskbarZone zone)
    {
        var layout = Layout();

        Assert.False(SectionOrdering.MoveToZone(layout, id, zone));

        Assert.Equal(Keys(Layout()), Keys(layout));
    }

    [Fact]
    public void CanShift_agrees_with_Shift_for_every_entry_and_direction()
    {
        foreach (var id in Layout().Select(p => p.Id).Append("missing"))
        {
            foreach (var direction in new[] { -1, 0, +1 })
            {
                var expected = SectionOrdering.CanShift(Layout(), id, direction);
                Assert.Equal(expected, SectionOrdering.Shift(Layout(), id, direction));
            }
        }
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
