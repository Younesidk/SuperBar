using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Geometry;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using SuperBar.Windows.Monitors;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.AppBar;

internal sealed class AppBarService(IMonitorService monitors, ILoggerFactory loggerFactory) : IAppBarService
{
    public IAppBarRegistration Register(nint windowHandle, AppBarOptions options) =>
        new AppBarRegistration(windowHandle, options, monitors, loggerFactory.CreateLogger<AppBarRegistration>());
}

/// <summary>
/// One registered AppBar. Subclasses the bar window (SetWindowSubclass) to receive the AppBar callback
/// message and to forward WM_ACTIVATE / WM_WINDOWPOSCHANGED to the shell as the AppBar contract requires.
/// </summary>
internal sealed class AppBarRegistration : IAppBarRegistration
{
    private const nuint SubclassId = 0x5B5B;
    private const nint RepositionRequest = 0x5B01;
    private const nint VerifyRequest = 0x5B02;

    private readonly nint _hwnd;
    private readonly IMonitorService _monitors;
    private readonly ILogger _logger;
    private readonly SubclassProc _subclassProc; // must stay referenced while subclassed
    private readonly WinEventProc _foregroundProc; // must stay referenced while hooked
    private nint _foregroundHook;
    private bool _shellReportsFullscreen;
    private readonly uint _callbackMessage;
    private AppBarOptions _options;
    private bool _registered;
    private bool _repositioning;
    private ScreenRect _lastQueried;
    private bool _disposed;

    public AppBarRegistration(nint hwnd, AppBarOptions options, IMonitorService monitors, ILogger logger)
    {
        _hwnd = hwnd;
        _options = options;
        _monitors = monitors;
        _logger = logger;
        _callbackMessage = RegisterWindowMessage("SuperBar.AppBarCallback");
        _subclassProc = SubclassWndProc;
        _foregroundProc = OnForegroundChanged;

        if (!SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0))
        {
            throw new InvalidOperationException("SetWindowSubclass failed for the SuperBar window");
        }

        Reregister();
    }

    public ScreenRect Bounds { get; private set; }

    public bool IsFullscreenAppActive { get; private set; }

    public event EventHandler<ScreenRect>? BoundsChanged;

    public event EventHandler<bool>? FullscreenAppChanged;

    public void Update(AppBarOptions options)
    {
        _options = options;
        Reposition();
    }

    public void Reregister()
    {
        if (_disposed)
        {
            return;
        }

        if (_registered)
        {
            // After an Explorer restart the shell has forgotten us; ABM_REMOVE is harmless either way.
            var remove = NewData();
            SHAppBarMessage(ABM_REMOVE, ref remove);
            _registered = false;
        }

        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        if (SHAppBarMessage(ABM_NEW, ref data) == 0)
        {
            _logger.LogWarning("ABM_NEW failed; the bar will be positioned but no screen space will be reserved");
        }
        else
        {
            _registered = true;
            _logger.LogInformation("Registered AppBar for window 0x{Handle:X}", _hwnd);
        }

        Reposition();
    }

    public void Reposition() => Reposition(onlyIfChanged: false);

    /// <summary>
    /// Places the bar on its monitor edge and reserves that strip with the shell. Every rectangle is computed from
    /// the monitor's full bounds (rcMonitor, never rcWork, which already excludes the bar and any taskbar) and the
    /// monitor's own DPI, in physical pixels. With <paramref name="onlyIfChanged"/> nothing is sent to the shell or
    /// moved while the shell's answer and the window's real rectangle still match the last placement; that path
    /// answers work-area broadcasts, which our own ABM_SETPOS also causes, so it must never loop.
    /// </summary>
    private void Reposition(bool onlyIfChanged)
    {
        if (_disposed || _repositioning)
        {
            return;
        }

        _repositioning = true;
        try
        {
            var monitor = ResolveMonitor();
            var thickness = AppBarGeometry.ToPhysicalThickness(_options.ThicknessDip, monitor.Scale);
            var edge = _options.Edge == ScreenEdge.Top ? ABE_TOP : ABE_BOTTOM;
            var proposed = AppBarGeometry.Propose(monitor.Bounds, _options.Edge, thickness);

            var final = proposed;
            if (_registered)
            {
                // ABM_QUERYPOS has no side effects: it only reports where the shell would put us.
                var data = NewData();
                data.uEdge = edge;
                data.rc = ToRECT(proposed);
                SHAppBarMessage(ABM_QUERYPOS, ref data);
                var queried = AppBarGeometry.EnforceThickness(MonitorService.ToRect(data.rc), _options.Edge, thickness);
                var negotiated = AppBarGeometry.Resolve(proposed, queried, _options.Edge, thickness, _options.OwnsEdge);

                if (onlyIfChanged && queried == _lastQueried && negotiated == Bounds && IsWindowAt(negotiated))
                {
                    return;
                }

                if (_options.OwnsEdge && queried != proposed && queried != _lastQueried)
                {
                    _logger.LogInformation(
                        "Shell reports part of the edge as taken (it would place the bar at {Queried}, e.g. a native taskbar that is still releasing its space); keeping the bar on the monitor edge at {Proposed}",
                        queried,
                        proposed);
                }

                _lastQueried = queried;
                data.rc = ToRECT(negotiated);
                SHAppBarMessage(ABM_SETPOS, ref data);
                final = AppBarGeometry.Resolve(
                    proposed,
                    AppBarGeometry.EnforceThickness(MonitorService.ToRect(data.rc), _options.Edge, thickness),
                    _options.Edge,
                    thickness,
                    _options.OwnsEdge);
            }
            else if (onlyIfChanged && final == Bounds && IsWindowAt(final))
            {
                return;
            }

            var insertAfter = IsFullscreenAppActive ? HWND_NOTOPMOST : HWND_TOPMOST;
            SetWindowPos(_hwnd, insertAfter, final.Left, final.Top, final.Width, final.Height, SWP_NOACTIVATE);

            if (final != Bounds)
            {
                Bounds = final;
                _logger.LogInformation(
                    "AppBar positioned at {Bounds} on {Monitor} {MonitorBounds} (scale {Scale:0.##}, owns edge: {OwnsEdge})",
                    final,
                    monitor.DeviceName,
                    monitor.Bounds,
                    monitor.Scale,
                    _options.OwnsEdge);
                BoundsChanged?.Invoke(this, final);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to position the AppBar");
        }
        finally
        {
            _repositioning = false;
        }
    }

    private bool IsWindowAt(ScreenRect rect) =>
        GetWindowRect(_hwnd, out var actual) && MonitorService.ToRect(actual) == rect;

    private MonitorInfo ResolveMonitor()
    {
        if (_options.MonitorHandle != 0 && MonitorService.TryGet(_options.MonitorHandle) is { } requested)
        {
            return requested;
        }

        return _monitors.GetPrimaryMonitor();
    }

    private nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (msg == _callbackMessage)
            {
                HandleCallback(wParam, lParam);
                return 0;
            }

            switch (msg)
            {
                case WM_ACTIVATE when _registered:
                {
                    var data = NewData();
                    SHAppBarMessage(ABM_ACTIVATE, ref data);
                    break;
                }

                case WM_WINDOWPOSCHANGED when _registered:
                {
                    var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                    var data = NewData();
                    SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
                    return result;
                }

                case WM_DPICHANGED:
                {
                    // Let the UI framework apply its DPI change first, then re-negotiate our physical size.
                    var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                    PostMessage(_hwnd, _callbackMessage, RepositionRequest, 0);
                    return result;
                }

                case WM_SETTINGCHANGE when (int)wParam == SPI_SETWORKAREA:
                case WM_DISPLAYCHANGE:
                    // The work area or the monitor's resolution changed (a native taskbar finishing its switch to
                    // auto-hide, another AppBar, a mode change): check the placement once the broadcast is over.
                    PostMessage(_hwnd, _callbackMessage, VerifyRequest, 0);
                    break;

                case WM_POWERBROADCAST when (int)wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND:
                    // Displays can come back from sleep in a different mode; re-anchor unconditionally.
                    PostMessage(_hwnd, _callbackMessage, RepositionRequest, 0);
                    break;

                case WM_NCDESTROY:
                    Unregister();
                    RemoveWindowSubclass(hWnd, _subclassProc, SubclassId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in AppBar window hook (message {Message})", msg);
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void HandleCallback(nint wParam, nint lParam)
    {
        switch ((int)wParam)
        {
            case (int)RepositionRequest:
            case ABN_POSCHANGED:
                // Deferred so we never re-enter the shell while it is notifying us.
                if ((int)wParam == ABN_POSCHANGED)
                {
                    PostMessage(_hwnd, _callbackMessage, RepositionRequest, 0);
                }
                else
                {
                    Reposition();
                }

                break;

            case (int)VerifyRequest:
                Reposition(onlyIfChanged: true);
                break;

            case ABN_FULLSCREENAPP:
                _shellReportsFullscreen = lParam != 0;
                UpdateForegroundHook();
                EvaluateFullscreen();
                break;
        }
    }

    /// <summary>
    /// The shell's report is not trusted on its own. It also reports "full-screen app" for the desktop itself
    /// (Win+D, Show desktop), because the desktop window covers the monitor, and then never reports it closed when
    /// normal windows come back. Dropping out of topmost on that report let the desktop cover the bar, and the stale
    /// flag would have hidden the bar behind the next ordinary window. So while the shell reports one, the
    /// foreground window must really cover this bar's monitor (and not be the desktop or SuperBar), re-checked on
    /// every foreground change.
    /// </summary>
    private void EvaluateFullscreen()
    {
        var foreground = GetForegroundWindow();
        var fullscreen = _shellReportsFullscreen && IsFullscreenWindow(foreground);
        if (fullscreen == IsFullscreenAppActive)
        {
            return;
        }

        IsFullscreenAppActive = fullscreen;
        _logger.LogInformation("Full-screen application {State}", fullscreen ? "opened" : "closed");
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (fullscreen)
        {
            // Leaving the topmost band keeps the bar at the top of the normal band, still above the full-screen
            // window: place it directly behind that window as well.
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetWindowPos(_hwnd, foreground, 0, 0, 0, 0, flags);
        }
        else
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
        }

        FullscreenAppChanged?.Invoke(this, fullscreen);
    }

    /// <summary>Watches foreground changes only while the shell reports a full-screen window.</summary>
    private void UpdateForegroundHook()
    {
        if (_shellReportsFullscreen && _foregroundHook == 0 && !_disposed)
        {
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        else if (!_shellReportsFullscreen && _foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            EvaluateFullscreen();
        }
        catch (Exception ex)
        {
            // Never let an exception unwind through a native frame.
            _logger.LogError(ex, "Failed to re-evaluate the full-screen state");
        }
    }

    private bool IsFullscreenWindow(nint hwnd)
    {
        if (hwnd == 0 || hwnd == _hwnd || IsDesktopWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST))
        {
            return false;
        }

        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        // Maximized windows stop at the work area (above the bar); only full-screen windows cover the whole monitor.
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    /// <summary>The desktop: Progman, or the WorkerW that hosts the icons and wallpaper.</summary>
    internal static bool IsDesktopWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var buffer = new char[32];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        var name = new string(buffer, 0, Math.Max(0, length));
        return name is "Progman" or "WorkerW";
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
    };

    private static RECT ToRECT(ScreenRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
        _logger.LogInformation("AppBar removed for window 0x{Handle:X}", _hwnd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }

        Unregister();
        if (IsWindow(_hwnd))
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }
    }
}
