using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Shell;

/// <summary>
/// A hidden, never-shown top-level window that receives shell broadcasts
/// ("TaskbarCreated", WM_DISPLAYCHANGE) and shell-hook notifications for window tracking.
/// Message-only windows (HWND_MESSAGE) cannot be used because they do not receive broadcasts.
/// Must be created on the UI thread so messages are pumped by the Avalonia dispatcher loop.
/// </summary>
internal sealed class ShellMessageWindow : IShellEvents, IDisposable
{
    private readonly ILogger<ShellMessageWindow> _logger;
    private readonly WndProc _wndProc; // keep the delegate alive for the lifetime of the window
    private readonly string _className = "SuperBar.ShellMessageWindow." + Guid.NewGuid().ToString("N");
    private readonly nint _instance = GetModuleHandle(null);
    private bool _disposed;

    public ShellMessageWindow(ILogger<ShellMessageWindow> logger)
    {
        _logger = logger;
        _wndProc = WindowProc;
        TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = _className,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
        }

        const uint WS_EX_TOOLWINDOW = 0x80;
        Handle = CreateWindowEx(WS_EX_TOOLWINDOW, _className, "SuperBar Shell Listener", WS_POPUP, 0, 0, 0, 0, 0, 0, _instance, 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        }

        // Allow the (unelevated) Explorer broadcast through even if SuperBar ever runs elevated.
        ChangeWindowMessageFilterEx(Handle, TaskbarCreatedMessage, MSGFLT_ALLOW, 0);
        _logger.LogDebug("Shell message window created: 0x{Handle:X}", Handle);
    }

    public nint Handle { get; }

    public uint TaskbarCreatedMessage { get; }

    public event EventHandler? ExplorerRestarted;

    public event EventHandler? DisplaySettingsChanged;

    /// <summary>Raw messages for other components (e.g. the shell hook used by the window tracker).</summary>
    public event Action<uint, nint, nint>? MessageReceived;

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == TaskbarCreatedMessage)
            {
                _logger.LogInformation("Explorer created its taskbar (Explorer started or restarted)");
                ExplorerRestarted?.Invoke(this, EventArgs.Empty);
            }
            else if (msg == WM_DISPLAYCHANGE)
            {
                _logger.LogInformation("Display configuration changed");
                DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            MessageReceived?.Invoke(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Never let an exception unwind through a native frame.
            _logger.LogError(ex, "Unhandled exception while processing shell message {Message}", msg);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyWindow(Handle);
        UnregisterClass(_className, _instance);
    }
}
