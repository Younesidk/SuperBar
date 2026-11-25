using SuperBar.Windows.Windows;

namespace SuperBar.Tests.Windows;

public class WindowChromeTests
{
    private const long WS_VISIBLE = 0x10000000;
    private const long WS_CLIPSIBLINGS = 0x04000000;
    private const long WS_DLGFRAME = 0x00400000;
    private const long WS_BORDER = 0x00800000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_WINDOWEDGE = 0x00000100;
    private const long WS_EX_APPWINDOW = 0x00040000;

    [Fact]
    public void Frame_bits_are_removed_and_other_bits_kept()
    {
        // What WinUI's presenter re-applies to a "borderless" window: a dialog frame and a system menu.
        var style = WS_VISIBLE | WS_CLIPSIBLINGS | WS_DLGFRAME | WS_BORDER | WS_SYSMENU | WS_THICKFRAME;

        Assert.Equal(WS_VISIBLE | WS_CLIPSIBLINGS, WindowChrome.SurfaceStyle(style));
    }

    [Fact]
    public void Surface_is_a_tool_window_without_edges()
    {
        var ex = WS_EX_TOPMOST | WS_EX_WINDOWEDGE | WS_EX_APPWINDOW;

        Assert.Equal(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WindowChrome.SurfaceExStyle(ex));
    }
}
