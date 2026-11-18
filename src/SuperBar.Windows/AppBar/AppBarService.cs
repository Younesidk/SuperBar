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

    private readonly nint _hwnd;
    private readonly IMonitorService _monitors;
    private readonly ILogger _logger;
    private readonly SubclassProc _subclassProc; // must stay referenced while subclassed
    private readonly uint _callbackMessage;
    private AppBarOptions _options;
    private bool _registered;
    private bool _repositioning;
    private bool _disposed;

    public AppBarRegistration(nint hwnd, AppBarOptions options, IMonitorService monitors, ILogger logger)
    {
        _hwnd = hwnd;
        _options = options;
        _monitors = monitors;
        _logger = logger;
        _callbackMessage = RegisterWindowMessage("SuperBar.AppBarCallback");
        _subclassProc = SubclassWndProc;

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

    public void Reposition()
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
                var data = NewData();
                data.uEdge = edge;
                data.rc = ToRECT(proposed);
                SHAppBarMessage(ABM_QUERYPOS, ref data);

                var adjusted = AppBarGeometry.EnforceThickness(MonitorService.ToRect(data.rc), _options.Edge, thickness);
                data.rc = ToRECT(adjusted);
                SHAppBarMessage(ABM_SETPOS, ref data);
                final = AppBarGeometry.EnforceThickness(MonitorService.ToRect(data.rc), _options.Edge, thickness);
            }

            var insertAfter = IsFullscreenAppActive ? HWND_NOTOPMOST : HWND_TOPMOST;
            SetWindowPos(_hwnd, insertAfter, final.Left, final.Top, final.Width, final.Height, SWP_NOACTIVATE);

            if (final != Bounds)
            {
                Bounds = final;
                _logger.LogInformation("AppBar positioned at {Bounds} on {Monitor} (scale {Scale:0.##})", final, monitor.DeviceName, monitor.Scale);
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

            case ABN_FULLSCREENAPP:
            {
                var fullscreen = lParam != 0;
                if (fullscreen == IsFullscreenAppActive)
                {
                    break;
                }

                IsFullscreenAppActive = fullscreen;
                _logger.LogInformation("Full-screen application {State}", fullscreen ? "opened" : "closed");
                SetWindowPos(_hwnd, fullscreen ? HWND_NOTOPMOST : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                FullscreenAppChanged?.Invoke(this, fullscreen);
                break;
            }
        }
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
        Unregister();
        if (IsWindow(_hwnd))
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }
    }
}
