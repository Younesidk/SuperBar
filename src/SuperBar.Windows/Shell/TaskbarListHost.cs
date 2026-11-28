using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Apps;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Shell;

/// <summary>
/// Receives the <c>ITaskbarList</c> calls apps make on their taskbar buttons, starting with overlay icons
/// (<c>ITaskbarList3::SetOverlayIcon</c>: Discord's unread badge, Outlook's envelope, …).
/// <para>
/// <c>ITaskbarList</c> runs inside the calling app and sends its requests to the window named by the
/// <c>TaskbandHWND</c> property of Explorer's taskbar (<c>Shell_TrayWnd</c>), normally Explorer's task band
/// (<c>MSTaskSwWClass</c>, also the shell's taskman window). While SuperBar replaces the native taskbar the property
/// points at the shell message window instead, as other replacement taskbars do, and it is pointed back at the task
/// band when the native taskbar is restored. <see cref="SuperBar.Windows.Taskbar.NativeTaskbarController"/> drives
/// <see cref="Claim"/> and <see cref="Release"/>, including after an Explorer restart (which creates a new taskbar).
/// </para>
/// </summary>
internal sealed class TaskbarListHost : IDisposable
{
    private const string TaskbandProperty = "TaskbandHWND";
    private const string TrayClass = "Shell_TrayWnd";

    // ITaskbarList → task band messages (wParam = the app's window).
    // Observed on Windows 11: SetOverlayIcon sends WM_USER+79 (lParam = HICON, 0 clears) followed by WM_USER+85
    // (the description). Older notes on Explorer's task band list +75, which this build does not use for overlays.
    private const uint SetOverlayIconMessage = WM_USER + 79;

    private readonly ShellMessageWindow _shell;
    private readonly ILogger<TaskbarListHost> _logger;
    private nint _taskband;
    private bool _listening;

    public TaskbarListHost(ShellMessageWindow shell, ILogger<TaskbarListHost> logger)
    {
        _shell = shell;
        _logger = logger;
    }

    /// <summary>An app set (pixels) or cleared (null) the overlay icon of one of its windows. Raised on the UI thread.</summary>
    public event Action<nint, IconPixels?>? OverlayIconChanged;

    /// <summary>ITaskbarList calls now reach SuperBar (see <see cref="RequestRefresh"/>).</summary>
    public event EventHandler? Claimed;

    /// <summary>Starts listening. Requests only arrive while claimed. UI thread.</summary>
    public void Start()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;

        // Apps are often unelevated; let their requests through if SuperBar ever runs elevated.
        ChangeWindowMessageFilterEx(_shell.Handle, SetOverlayIconMessage, MSGFLT_ALLOW, 0);
        _shell.MessageReceived += OnMessage;
    }

    /// <summary>Routes ITaskbarList calls to SuperBar, remembering Explorer's task band so it can be handed back.</summary>
    public void Claim()
    {
        var tray = FindWindow(TrayClass, null);
        if (tray == 0)
        {
            _logger.LogWarning("Explorer's taskbar was not found; app badges (overlay icons) will not show");
            return;
        }

        var current = GetProp(tray, TaskbandProperty);
        if (current == _shell.Handle)
        {
            return;
        }

        // A SuperBar that crashed leaves a dead window here: the shell's taskman window is the task band itself.
        _taskband = IsWindow(current) ? current : GetTaskmanWindow();
        if (!SetProp(tray, TaskbandProperty, _shell.Handle))
        {
            _logger.LogWarning("Could not redirect taskbar button updates ({Error}); app badges will not show", Marshal.GetLastWin32Error());
            return;
        }

        _logger.LogInformation("Receiving taskbar button updates (Explorer task band 0x{Taskband:X})", _taskband);
        Claimed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hands ITaskbarList back to Explorer. Also repairs a redirect left behind by a SuperBar that crashed.
    /// Safe to call from any thread and more than once.
    /// </summary>
    public void Release()
    {
        var tray = FindWindow(TrayClass, null);
        if (tray == 0)
        {
            return;
        }

        var current = GetProp(tray, TaskbandProperty);
        if (current != _shell.Handle && IsWindow(current))
        {
            return; // already Explorer's
        }

        var target = IsWindow(_taskband) ? _taskband : GetTaskmanWindow();
        if (target != 0 && SetProp(tray, TaskbandProperty, target))
        {
            _logger.LogInformation("Returned taskbar button updates to Explorer (0x{Target:X})", target);
        }
    }

    /// <summary>
    /// Asks windows to re-send their taskbar button state: overlay icons set before SuperBar claimed the task band
    /// went to Explorer. This is the message Explorer sends when it creates a button; apps answer it by re-applying
    /// their overlay icon, progress and thumbnail buttons.
    /// </summary>
    public void RequestRefresh(IEnumerable<nint> windows)
    {
        var message = RegisterWindowMessage("TaskbarButtonCreated");
        foreach (var hwnd in windows)
        {
            SendNotifyMessage(hwnd, message, 0, 0);
        }
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != SetOverlayIconMessage)
        {
            return;
        }

        // The app may destroy the icon as soon as the call returns: convert it now, while the handle is valid.
        var pixels = lParam != 0 ? IconProvider.FromHIcon(lParam) : null;
        _logger.LogDebug("Overlay icon {Action} for window 0x{Handle:X}", pixels is null ? "cleared" : "set", wParam);
        OverlayIconChanged?.Invoke(wParam, pixels);
    }

    public void Dispose()
    {
        if (_listening)
        {
            _listening = false;
            _shell.MessageReceived -= OnMessage;
        }

        Release();
    }
}
