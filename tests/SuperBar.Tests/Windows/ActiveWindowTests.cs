using SuperBar.Windows.Windows;

namespace SuperBar.Tests.Windows;

public class ActiveWindowTests
{
    private const nint App = 100;
    private const nint OtherApp = 200;
    private const nint Bar = 900;
    private const nint Desktop = 800;

    private static nint Resolve(nint current, nint activated, bool tracked, nint foreground, bool minimized = false) =>
        WindowTracker.ResolveActiveWindow(current, activated, tracked, foreground, h => h == Bar, _ => minimized);

    [Fact]
    public void A_tracked_window_becomes_active() => Assert.Equal(OtherApp, Resolve(App, OtherApp, tracked: true, OtherApp));

    [Fact]
    public void Clicking_the_bar_keeps_the_active_window()
    {
        // The shell reports 0 for the bar because it has no taskbar button.
        Assert.Equal(App, Resolve(App, 0, tracked: false, Bar));
    }

    [Fact]
    public void A_transient_empty_foreground_keeps_the_active_window() => Assert.Equal(App, Resolve(App, 0, tracked: false, 0));

    [Fact]
    public void A_minimized_window_is_not_active_while_the_bar_has_focus() =>
        Assert.Equal(0, Resolve(App, 0, tracked: false, Bar, minimized: true));

    [Fact]
    public void The_desktop_or_a_shell_flyout_clears_the_active_window()
    {
        Assert.Equal(0, Resolve(App, 0, tracked: false, Desktop));
        Assert.Equal(0, Resolve(App, Desktop, tracked: false, Desktop));
    }
}
