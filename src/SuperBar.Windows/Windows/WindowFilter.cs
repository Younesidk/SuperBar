using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Windows;

internal readonly record struct WindowTraits(
    bool IsVisible,
    bool IsCloaked,
    bool HasOwner,
    long ExtendedStyle,
    int TitleLength,
    string ClassName);

/// <summary>
/// The classic "would this window get a taskbar button" rules (same rules Explorer and Alt+Tab apply).
/// Pure so it can be unit-tested without real windows.
/// </summary>
internal static class WindowFilter
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", // hosted UWP surfaces; their ApplicationFrameWindow is listed instead
    };

    public static bool IsTaskbarCandidate(in WindowTraits t)
    {
        if (!t.IsVisible || t.IsCloaked || t.TitleLength == 0 || ShellClasses.Contains(t.ClassName))
        {
            return false;
        }

        var appWindow = (t.ExtendedStyle & WS_EX_APPWINDOW) != 0;
        if (appWindow)
        {
            return true;
        }

        if ((t.ExtendedStyle & WS_EX_TOOLWINDOW) != 0 || (t.ExtendedStyle & WS_EX_NOACTIVATE) != 0)
        {
            return false;
        }

        return !t.HasOwner;
    }
}
