using SuperBar.Core.Geometry;
using SuperBar.Core.Shell;
using SuperBar.Windows.AppBar;
using SuperBar.Windows.Windows;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Tests.Windows;

public class AppBarGeometryTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void Proposes_a_full_width_strip_on_the_bottom_edge()
    {
        var r = AppBarGeometry.Propose(Monitor, ScreenEdge.Bottom, 78);

        Assert.Equal(new ScreenRect(0, 1362, 2560, 1440), r);
    }

    [Fact]
    public void Works_on_monitors_with_negative_origins()
    {
        var left = new ScreenRect(-1920, 120, 0, 1200);

        var r = AppBarGeometry.Propose(left, ScreenEdge.Bottom, 48);

        Assert.Equal(-1920, r.Left);
        Assert.Equal(1152, r.Top);
        Assert.Equal(1920, r.Width);
    }

    [Fact]
    public void Keeps_thickness_when_the_shell_moves_the_docking_edge()
    {
        // Another AppBar owns the bottom 40 px; ABM_QUERYPOS moves our bottom up but returns a collapsed height.
        var queried = new ScreenRect(0, 1362, 2560, 1400);

        var r = AppBarGeometry.EnforceThickness(queried, ScreenEdge.Bottom, 78);

        Assert.Equal(new ScreenRect(0, 1322, 2560, 1400), r);
    }

    [Fact]
    public void Owning_the_edge_ignores_a_shell_answer_that_lifts_the_bar()
    {
        // Native taskbar just switched to auto-hide but Explorer still reports its 60 px (48 DIP @ 125%) strip.
        var proposed = AppBarGeometry.Propose(Monitor, ScreenEdge.Bottom, 65);
        var queried = AppBarGeometry.EnforceThickness(new ScreenRect(0, 1315, 2560, 1380), ScreenEdge.Bottom, 65);

        var r = AppBarGeometry.Resolve(proposed, queried, ScreenEdge.Bottom, 65, ownsEdge: true);

        Assert.Equal(new ScreenRect(0, 1375, 2560, 1440), r);
        Assert.Equal(Monitor.Bottom, r.Bottom);
    }

    [Fact]
    public void Without_owning_the_edge_the_bar_stacks_above_a_visible_taskbar()
    {
        var proposed = AppBarGeometry.Propose(Monitor, ScreenEdge.Bottom, 52);
        var queried = new ScreenRect(0, 1340, 2560, 1392);

        var r = AppBarGeometry.Resolve(proposed, queried, ScreenEdge.Bottom, 52, ownsEdge: false);

        Assert.Equal(new ScreenRect(0, 1340, 2560, 1392), r);
    }

    [Theory]
    [InlineData(0, 0, 1280, 720, 1.0)]
    [InlineData(0, 0, 1366, 768, 1.0)]
    [InlineData(0, 0, 1600, 900, 1.25)]
    [InlineData(0, 0, 1920, 1080, 1.5)]
    [InlineData(0, 0, 2560, 1440, 1.75)]
    [InlineData(0, 0, 3840, 2160, 2.5)]
    [InlineData(-3440, -200, 0, 1240, 1.25)]
    public void Owned_bar_is_flush_with_the_monitor_at_any_resolution_and_scale(int left, int top, int right, int bottom, double scale)
    {
        var monitor = new ScreenRect(left, top, right, bottom);
        var thickness = AppBarGeometry.ToPhysicalThickness(52, scale);
        var proposed = AppBarGeometry.Propose(monitor, ScreenEdge.Bottom, thickness);
        var lifted = new ScreenRect(left, bottom - 48 - thickness, right, bottom - 48);

        var r = AppBarGeometry.Resolve(proposed, lifted, ScreenEdge.Bottom, thickness, ownsEdge: true);

        Assert.Equal(monitor.Bottom, r.Bottom);
        Assert.Equal(monitor.Left, r.Left);
        Assert.Equal(monitor.Right, r.Right);
        Assert.Equal(thickness, r.Height);
    }

    [Theory]
    [InlineData(52, 1.0, 52)]
    [InlineData(52, 1.25, 65)]
    [InlineData(52, 1.5, 78)]
    [InlineData(52, 1.75, 91)]
    [InlineData(48, 0, 48)]
    public void Converts_dips_to_physical_pixels(double dip, double scale, int expected)
    {
        Assert.Equal(expected, AppBarGeometry.ToPhysicalThickness(dip, scale));
    }
}

public class WindowFilterTests
{
    private static WindowTraits Normal(long exStyle = 0, bool owner = false, bool cloaked = false, bool visible = true, int title = 5, string cls = "Notepad") =>
        new(visible, cloaked, owner, exStyle, title, cls);

    [Fact]
    public void Regular_top_level_window_is_listed() => Assert.True(WindowFilter.IsTaskbarCandidate(Normal()));

    [Fact]
    public void Tool_windows_are_not_listed() => Assert.False(WindowFilter.IsTaskbarCandidate(Normal(WS_EX_TOOLWINDOW)));

    [Fact]
    public void Owned_windows_are_not_listed() => Assert.False(WindowFilter.IsTaskbarCandidate(Normal(owner: true)));

    [Fact]
    public void App_window_style_forces_listing_even_when_owned() =>
        Assert.True(WindowFilter.IsTaskbarCandidate(Normal(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW, owner: true)));

    [Fact]
    public void Cloaked_windows_are_not_listed() => Assert.False(WindowFilter.IsTaskbarCandidate(Normal(cloaked: true)));

    [Fact]
    public void Invisible_and_untitled_windows_are_not_listed()
    {
        Assert.False(WindowFilter.IsTaskbarCandidate(Normal(visible: false)));
        Assert.False(WindowFilter.IsTaskbarCandidate(Normal(title: 0)));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void Shell_surfaces_are_not_listed(string cls) => Assert.False(WindowFilter.IsTaskbarCandidate(Normal(cls: cls)));
}
