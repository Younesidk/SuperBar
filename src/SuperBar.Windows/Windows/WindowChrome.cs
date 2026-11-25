using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Windows;

/// <summary>Win32 chrome for SuperBar's own windows (see <see cref="IWindowChrome"/>).</summary>
internal sealed class WindowChrome(ILogger<WindowChrome> logger) : IWindowChrome, IDisposable
{
    private const nuint SubclassId = 0x5B5C;

    // One static subclass procedure for every surface: Set/RemoveWindowSubclass match on the function pointer,
    // so the same delegate instance (and native thunk) must be used for both, and it must stay alive.
    private static readonly SubclassProc s_subclassProc = SurfaceSubclassProc;
    private readonly HashSet<nint> _subclassed = new();

    /// <summary>
    /// Raised on the UI thread while a mouse-down is activating one of SuperBar's surfaces, i.e. before the shell
    /// flyout that click is about to light-dismiss (Start, tray overflow) has closed. Handlers must not throw.
    /// </summary>
    internal static event Action? SurfaceClickActivating;

    public void ApplyShellSurfaceStyle(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        // Subclass first: from now on WM_STYLECHANGING keeps the frame off and the tool-window bit on, even when the
        // UI framework re-applies its own styles later (WinUI's presenter does, e.g. when the window is shown).
        // The subclass also removes the system minimum track size so heights down to AppearanceSettings.MinHeight work.
        if (_subclassed.Add(windowHandle) && !SetWindowSubclass(windowHandle, s_subclassProc, SubclassId, 0))
        {
            _subclassed.Remove(windowHandle);
            logger.LogWarning("SetWindowSubclass failed for window 0x{Handle:X}; frame styles and minimum size are not enforced", windowHandle);
        }

        // Tool window: never in Alt+Tab / Win+Tab / the taskbar. WS_EX_NOACTIVATE is deliberately NOT used:
        // it would break flyouts, keyboard focus and SetForegroundWindow from clicks on the bar.
        SetWindowLongPtr(windowHandle, GWL_EXSTYLE, (nint)SurfaceExStyle((long)GetWindowLongPtr(windowHandle, GWL_EXSTYLE)));

        // No caption, dialog/sizing frame or system menu: they add a non-client border and a minimum height.
        SetWindowLongPtr(windowHandle, GWL_STYLE, (nint)SurfaceStyle((long)GetWindowLongPtr(windowHandle, GWL_STYLE)));
        SetWindowPos(windowHandle, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Windows 11: sharp, flush edges and no 1px DWM outline. Both attributes fail harmlessly on Windows 10.
        var corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(windowHandle, DWMWA_BORDER_COLOR, ref border, sizeof(uint));

        logger.LogDebug("Applied shell-surface chrome to window 0x{Handle:X}", windowHandle);
    }

    public void ShowWithoutActivation(nint windowHandle) => ShowWindow(windowHandle, SW_SHOWNOACTIVATE);

    public double GetScale(nint windowHandle)
    {
        var dpi = windowHandle == 0 ? 0 : GetDpiForWindow(windowHandle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    internal static long SurfaceStyle(long style) =>
        style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);

    internal static long SurfaceExStyle(long exStyle) =>
        (exStyle | WS_EX_TOOLWINDOW) & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);

    private static nint SurfaceSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        switch (msg)
        {
            case WM_STYLECHANGING:
            {
                // Let the framework's window procedure run first (WinUI forces its own frame bits here),
                // then have the last word on the style that is applied.
                var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                var change = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
                var wanted = (int)wParam switch
                {
                    GWL_STYLE => (uint)SurfaceStyle(change.styleNew),
                    GWL_EXSTYLE => (uint)SurfaceExStyle(change.styleNew),
                    _ => change.styleNew,
                };

                if (wanted != change.styleNew)
                {
                    change.styleNew = wanted;
                    Marshal.StructureToPtr(change, lParam, fDeleteOld: false);
                }

                return result;
            }

            case WM_GETMINMAXINFO:
            {
                var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize = new POINT { X = 1, Y = 1 };
                Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
                return result;
            }

            case WM_MOUSEACTIVATE:
            case WM_ACTIVATE when ((int)wParam & 0xFFFF) == WA_CLICKACTIVE:
                SurfaceClickActivating?.Invoke();
                break;

            case WM_NCDESTROY:
                RemoveWindowSubclass(hWnd, s_subclassProc, SubclassId);
                break;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        foreach (var hwnd in _subclassed)
        {
            if (IsWindow(hwnd))
            {
                RemoveWindowSubclass(hwnd, s_subclassProc, SubclassId);
            }
        }

        _subclassed.Clear();
    }
}
