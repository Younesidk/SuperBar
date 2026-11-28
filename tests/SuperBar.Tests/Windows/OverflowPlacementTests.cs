using SuperBar.Windows.Apps;
using SuperBar.Windows.Interop;

namespace SuperBar.Tests.Windows;

public class OverflowPlacementTests
{
    // 1920x1080 monitor with a 52px bar; Explorer puts the 234x154 flyout at the bottom of the screen.
    private static readonly RECT Flyout = new(1528, 924, 1762, 1078);

    [Fact]
    public void Click_on_a_bottom_bar_centers_the_flyout_above_it()
    {
        var work = new RECT(0, 0, 1920, 1028);

        var p = OverflowPlacement.Compute(Flyout, work, new POINT { X = 1600, Y = 1050 });

        Assert.Equal(1600 - 117, p.X);
        Assert.Equal(1028 - 154, p.Y);
    }

    [Fact]
    public void Click_on_a_top_bar_puts_the_flyout_below_it()
    {
        var work = new RECT(0, 52, 1920, 1080);

        var p = OverflowPlacement.Compute(Flyout, work, new POINT { X = 1600, Y = 20 });

        Assert.Equal(52, p.Y);
    }

    [Fact]
    public void Flyout_stays_inside_the_work_area_near_the_screen_edge()
    {
        var work = new RECT(0, 0, 1920, 1028);

        var p = OverflowPlacement.Compute(Flyout, work, new POINT { X = 1915, Y = 1050 });

        Assert.Equal(1920 - 234, p.X);
    }

    [Fact]
    public void Keyboard_invocation_only_moves_the_flyout_off_the_bar()
    {
        var work = new RECT(0, 0, 1920, 1028);

        var p = OverflowPlacement.Compute(Flyout, work, new POINT { X = 400, Y = 300 });

        Assert.Equal(1528, p.X);
        Assert.Equal(1028 - 154, p.Y);
    }
}
