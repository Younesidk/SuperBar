using SuperBar.Core.Layout;

namespace SuperBar.Tests.Core;

public class ZoneLayoutCalculatorTests
{
    [Fact]
    public void Center_zone_is_centered_on_the_whole_bar_when_it_fits()
    {
        var a = ZoneLayoutCalculator.Compute(1000, leftWidth: 100, centerWidth: 200, rightWidth: 150);

        Assert.Equal(0, a.LeftX);
        Assert.Equal(400, a.CenterX);
        Assert.Equal(850, a.RightX);
    }

    [Fact]
    public void Center_zone_is_pushed_right_instead_of_overlapping_a_wide_left_zone()
    {
        var a = ZoneLayoutCalculator.Compute(1000, leftWidth: 500, centerWidth: 200, rightWidth: 100);

        Assert.Equal(500, a.CenterX);
        Assert.True(a.CenterX + a.CenterWidth <= a.RightX);
    }

    [Fact]
    public void Right_zone_stays_anchored_to_the_end_edge_when_space_runs_out()
    {
        var a = ZoneLayoutCalculator.Compute(500, leftWidth: 400, centerWidth: 200, rightWidth: 200);

        Assert.Equal(300, a.RightX);
        Assert.Equal(300, a.LeftWidth); // left zone is clipped, never the system section
        Assert.Equal(0, a.CenterWidth);
    }

    [Fact]
    public void Negative_and_zero_inputs_are_safe()
    {
        var a = ZoneLayoutCalculator.Compute(0, -5, -5, -5);

        Assert.Equal(0, a.LeftWidth);
        Assert.Equal(0, a.CenterWidth);
        Assert.Equal(0, a.RightWidth);
    }
}
