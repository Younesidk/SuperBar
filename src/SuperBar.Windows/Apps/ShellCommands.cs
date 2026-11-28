using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using SuperBar.Windows.Windows;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Apps;

/// <summary>
/// Drives built-in shell surfaces (owned by Explorer / ShellExperienceHost) through their system shortcuts.
///
/// Start, the tray overflow, Quick Settings and the notification center are light-dismiss flyouts: the mouse-down
/// that lands on SuperBar already closes them, so blindly sending the shortcut afterwards would reopen them and the button could never close what it
/// opened. <see cref="WindowChrome.SurfaceActivated"/> fires as that mouse-down activates the bar, before the click
/// reaches the button; the state captured there decides whether the click means "close" (nothing left to do) or
/// "open". Start and the overflow can be queried directly; Quick Settings and the notification center are
/// recognised as the window the bar took the foreground from, which a foreground hook keeps track of.
/// </summary>
internal sealed class ShellCommands : IShellCommands, IDisposable
{
    private const ushort VK_N = 0x4E;
    private const ushort VK_A = 0x41;
    private const ushort VK_B = 0x42;
    private const ushort VK_RETURN = 0x0D;

    private const string TrayClass = "Shell_TrayWnd";
    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";
    private const string QuickSettingsClass = "ControlCenterWindow";

    /// <summary>Hosts the notification center / calendar flyout.</summary>
    private const string NotificationHostProcess = "ShellExperienceHost.exe";

    /// <summary>How long a captured click state stays valid: mouse-down to the button's command (it fires on press).</summary>
    private const long ClickMemoryMs = 400;

    /// <summary>How long a shell flyout needs to finish closing before the shell accepts the next shortcut.</summary>
    private const int FlyoutCloseMs = 250;

    private static readonly Guid AppVisibilityClsid = new("7E5FE3D9-985F-4908-91F9-EE19F9FD1514");

    private readonly ILogger<ShellCommands> _logger;
    private IAppVisibility? _appVisibility;
    private bool _appVisibilityUnavailable;
    private long _clickTick = -ClickMemoryMs;
    private bool _clickDismissedStart;
    private bool _clickDismissedOverflow;
    private bool _clickDismissedQuickSettings;
    private bool _clickDismissedNotifications;
    private bool _openingOverflow;
    private readonly WinEventProc _foregroundHookProc; // must stay referenced while hooked
    private readonly nint _foregroundHook;
    private nint _lastForeignForeground;

    /// <summary>Must be created on the UI thread: the foreground hook is delivered through its message loop.</summary>
    public ShellCommands(ILogger<ShellCommands> logger)
    {
        _logger = logger;
        _foregroundHookProc = (_, _, hwnd, _, _, _, _) => _lastForeignForeground = hwnd;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundHookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == 0)
        {
            _logger.LogWarning("Could not install the foreground hook; Quick Settings and the notification center may reopen when their button is clicked to close them");
        }

        WindowChrome.SurfaceActivated += OnSurfaceActivated;
    }

    public void ToggleStartMenu()
    {
        if (TakeClickFlag(ref _clickDismissedStart))
        {
            // The click itself is closing Start. Only step in if Start ignored it.
            _ = CloseStartIfStillOpenAsync();
            return;
        }

        // The Windows key toggles: it opens Start, or closes it when invoked from the keyboard.
        SendWhenSettled(VK_LWIN);
    }

    public void ToggleTrayOverflow()
    {
        if (TakeClickFlag(ref _clickDismissedOverflow))
        {
            return;
        }

        var overflow = FindWindow(OverflowClass, null);
        if (overflow != 0 && IsWindowVisible(overflow))
        {
            if (GetForegroundWindow() == overflow)
            {
                SendChord(VK_ESCAPE);
            }
            else
            {
                ShowWindow(overflow, SW_HIDE);
            }

            return;
        }

        _ = OpenTrayOverflowAsync(AnotherFlyoutIsClosing());
    }

    public void ToggleNotificationCenter()
    {
        if (!TakeClickFlag(ref _clickDismissedNotifications))
        {
            SendWhenSettled(VK_LWIN, VK_N);
        }
    }

    public void ToggleQuickSettings()
    {
        if (!TakeClickFlag(ref _clickDismissedQuickSettings))
        {
            SendWhenSettled(VK_LWIN, VK_A);
        }
    }

    public void OpenTaskManager() => SendChord(VK_CONTROL, VK_SHIFT, VK_ESCAPE);

    /// <summary>Runs inside the activation of a SuperBar surface; must not throw.</summary>
    private void OnSurfaceActivated()
    {
        try
        {
            var startOpen = IsStartMenuOpen();
            var overflow = FindWindow(OverflowClass, null);
            var overflowOpen = overflow != 0 && IsWindowVisible(overflow);

            // Quick Settings and the notification center have no visibility API; they are identified as the
            // window this activation takes the foreground from (the last foreground window of another process).
            var displaced = _lastForeignForeground;
            var quickSettingsOpen = false;
            var notificationsOpen = false;
            if (displaced != 0 && IsWindowVisible(displaced))
            {
                quickSettingsOpen = ClassNameOf(displaced) == QuickSettingsClass;
                notificationsOpen = !quickSettingsOpen && ProcessNameOf(displaced).Equals(NotificationHostProcess, StringComparison.OrdinalIgnoreCase);
            }

            // One click can activate more than once; the flyout may already be gone the second time, so merge
            // instead of overwriting.
            var now = Environment.TickCount64;
            if (now - _clickTick <= ClickMemoryMs)
            {
                _clickDismissedStart |= startOpen;
                _clickDismissedOverflow |= overflowOpen;
                _clickDismissedQuickSettings |= quickSettingsOpen;
                _clickDismissedNotifications |= notificationsOpen;
            }
            else
            {
                _clickTick = now;
                _clickDismissedStart = startOpen;
                _clickDismissedOverflow = overflowOpen;
                _clickDismissedQuickSettings = quickSettingsOpen;
                _clickDismissedNotifications = notificationsOpen;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not capture shell flyout state for a click");
        }
    }

    private bool TakeClickFlag(ref bool flag)
    {
        var set = flag && Environment.TickCount64 - _clickTick <= ClickMemoryMs;
        flag = false;
        return set;
    }

    /// <summary>
    /// True when the current click is closing a flyout other than the one being asked for (the caller has already
    /// taken its own flag). The shell drops or undoes a shortcut that arrives while that flyout is still closing.
    /// </summary>
    private bool AnotherFlyoutIsClosing() =>
        Environment.TickCount64 - _clickTick <= ClickMemoryMs
        && (_clickDismissedStart || _clickDismissedOverflow || _clickDismissedQuickSettings || _clickDismissedNotifications);

    private void SendWhenSettled(params ushort[] keys)
    {
        if (AnotherFlyoutIsClosing())
        {
            _ = SendLaterAsync(keys);
        }
        else
        {
            SendChord(keys);
        }
    }

    private async Task SendLaterAsync(ushort[] keys)
    {
        await Task.Delay(FlyoutCloseMs);
        SendChord(keys);
    }

    private static string ClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private static string ProcessNameOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = pid == 0 ? 0 : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return "";
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? Path.GetFileName(new string(buffer, 0, (int)size)) : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private async Task CloseStartIfStillOpenAsync()
    {
        try
        {
            await Task.Delay(350);
            if (IsStartMenuOpen())
            {
                SendChord(VK_LWIN);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Start menu close check failed");
        }
    }

    private bool IsStartMenuOpen()
    {
        if (_appVisibilityUnavailable)
        {
            return false;
        }

        try
        {
            _appVisibility ??= (IAppVisibility)Activator.CreateInstance(Type.GetTypeFromCLSID(AppVisibilityClsid, throwOnError: true)!)!;
            return _appVisibility.IsLauncherVisible(out var visible) >= 0 && visible;
        }
        catch (Exception ex)
        {
            // Without it the Start button degrades to "always send the Windows key".
            _appVisibilityUnavailable = true;
            _logger.LogWarning(ex, "IAppVisibility is unavailable; the Start button cannot tell whether Start is open");
            return false;
        }
    }

    /// <summary>
    /// SuperBar does not host notification icons itself, so it opens Explorer's own overflow flyout: Win+B puts
    /// keyboard focus on the native "Show hidden icons" chevron (this works while the native taskbar is hidden) and
    /// Enter invokes it. The flyout is then moved next to SuperBar, because Explorer anchors it to its own taskbar.
    /// </summary>
    private async Task OpenTrayOverflowAsync(bool waitForOtherFlyout)
    {
        if (_openingOverflow)
        {
            return;
        }

        _openingOverflow = true;
        try
        {
            if (waitForOtherFlyout)
            {
                await Task.Delay(FlyoutCloseMs);
            }

            var tray = FindWindow(TrayClass, null);
            if (tray == 0)
            {
                _logger.LogWarning("Native taskbar window not found; cannot open the tray overflow");
                return;
            }

            GetCursorPos(out var cursor);
            SendChord(VK_LWIN, VK_B);

            // Enter must only ever go to the native tray, never to whatever application had focus.
            if (!await WaitForAsync(() => GetForegroundWindow() == tray, 600))
            {
                _logger.LogWarning("The native tray did not take focus after Win+B; tray overflow not opened");
                return;
            }

            await Task.Delay(40);
            SendChord(VK_RETURN);

            nint overflow = 0;
            if (!await WaitForAsync(() => (overflow = FindWindow(OverflowClass, null)) != 0 && IsWindowVisible(overflow), 800))
            {
                _logger.LogWarning("The tray overflow flyout did not appear (there may be no hidden icons)");
                return;
            }

            PlaceOverflow(overflow, cursor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open the tray overflow");
        }
        finally
        {
            _openingOverflow = false;
        }
    }

    /// <summary>Keeps the flyout off the bar; when the bar was clicked, also centers it on the click.</summary>
    private static void PlaceOverflow(nint overflow, POINT cursor)
    {
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetWindowRect(overflow, out var rect) || !GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info))
        {
            return;
        }

        var target = OverflowPlacement.Compute(rect, info.rcWork, cursor);
        if (target.X != rect.Left || target.Y != rect.Top)
        {
            SetWindowPos(overflow, 0, target.X, target.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(15);
        }

        return true;
    }

    private void SendChord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], up: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            _logger.LogWarning("SendInput sent {Sent}/{Total} events", sent, inputs.Length);
        }
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    public void Dispose()
    {
        WindowChrome.SurfaceActivated -= OnSurfaceActivated;
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
        }
    }
}

/// <summary>Pure placement math for the native tray overflow flyout (physical pixels).</summary>
internal static class OverflowPlacement
{
    /// <param name="flyout">Where Explorer put the flyout.</param>
    /// <param name="workArea">The monitor's work area, i.e. the screen minus SuperBar.</param>
    /// <param name="cursor">The pointer when the flyout was requested; outside the work area means "on the bar".</param>
    public static POINT Compute(RECT flyout, RECT workArea, POINT cursor)
    {
        var width = flyout.Right - flyout.Left;
        var height = flyout.Bottom - flyout.Top;
        var x = flyout.Left;
        int y;

        if (cursor.Y >= workArea.Bottom)
        {
            x = cursor.X - width / 2;
            y = workArea.Bottom - height;
        }
        else if (cursor.Y < workArea.Top)
        {
            x = cursor.X - width / 2;
            y = workArea.Top;
        }
        else
        {
            // Keyboard-invoked: leave it where Explorer put it, just off the bar.
            y = Math.Max(workArea.Top, Math.Min(flyout.Top, workArea.Bottom - height));
        }

        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
        return new POINT { X = x, Y = y };
    }
}

/// <summary>Shell COM API that reports whether the Start menu ("launcher") is showing.</summary>
[ComImport]
[Guid("2246EA2D-CAEA-4444-A3C4-6DE827E44313")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAppVisibility
{
    [PreserveSig]
    int GetAppVisibilityOnMonitor(nint hMonitor, out int pMode);

    [PreserveSig]
    int IsLauncherVisible([MarshalAs(UnmanagedType.Bool)] out bool pfVisible);

    [PreserveSig]
    int Advise(nint pCallback, out uint pdwCookie);

    [PreserveSig]
    int Unadvise(uint dwCookie);
}
