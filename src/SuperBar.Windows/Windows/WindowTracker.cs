using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Shell;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Windows;

/// <summary>
/// Tracks taskbar-eligible top-level windows. Event-driven: one EnumWindows pass at start-up,
/// then shell-hook notifications (RegisterShellHookWindow) — the same feed Explorer's taskbar uses.
/// </summary>
internal sealed class WindowTracker : IWindowTracker, IDisposable
{
    private readonly ShellMessageWindow _shell;
    private readonly TaskbarListHost _taskbarList;
    private readonly ILogger<WindowTracker> _logger;
    private readonly List<TrackedWindow> _windows = new();
    private readonly HashSet<nint> _ignored = new();
    private readonly Dictionary<int, string?> _processPaths = new();
    private uint _shellHookMessage;
    private bool _started;

    public WindowTracker(ShellMessageWindow shell, TaskbarListHost taskbarList, ILogger<WindowTracker> logger)
    {
        _shell = shell;
        _taskbarList = taskbarList;
        _logger = logger;
    }

    public IReadOnlyList<TrackedWindow> Windows => _windows;

    public nint ActiveWindow { get; private set; }

    public event EventHandler? WindowsChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _shellHookMessage = RegisterWindowMessage("SHELLHOOK");
        if (!RegisterShellHookWindow(_shell.Handle))
        {
            _logger.LogWarning("RegisterShellHookWindow failed; running windows will not update live");
        }

        _shell.MessageReceived += OnMessage;

        EnumWindows((hwnd, _) =>
        {
            TryAdd(hwnd, raise: false);
            return true;
        }, 0);

        var fg = GetForegroundWindow();
        if (IndexOf(fg) >= 0)
        {
            ActiveWindow = fg;
        }

        _taskbarList.OverlayIconChanged += OnOverlayIconChanged;
        _taskbarList.Claimed += OnTaskbarListClaimed;
        _taskbarList.Start();

        _logger.LogInformation("Window tracker started with {Count} windows", _windows.Count);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTaskbarListClaimed(object? sender, EventArgs e) =>
        _taskbarList.RequestRefresh(_windows.Select(w => w.Handle).ToList());

    private void OnOverlayIconChanged(nint hwnd, IconPixels? icon)
    {
        var index = IndexOf(hwnd);
        if (index < 0)
        {
            return;
        }

        _windows[index] = _windows[index] with { OverlayIcon = icon };
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A window called FlashWindowEx. Like Explorer, the button keeps asking for attention until the window is
    /// activated, even after the app stops flashing its caption. The active window cannot ask for attention.
    /// </summary>
    private void OnFlash(nint hwnd)
    {
        var index = IndexOf(hwnd);
        if (index < 0 || _windows[index].IsFlashing || hwnd == GetForegroundWindow())
        {
            return;
        }

        _logger.LogDebug("Window 0x{Handle:X} asks for attention", hwnd);
        _windows[index] = _windows[index] with { IsFlashing = true };
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopFlashing(nint hwnd)
    {
        var index = IndexOf(hwnd);
        if (index >= 0 && _windows[index].IsFlashing)
        {
            _windows[index] = _windows[index] with { IsFlashing = false };
        }
    }

    /// <summary>Excludes one of SuperBar's own surfaces from tracking.</summary>
    public void Ignore(nint hwnd)
    {
        _ignored.Add(hwnd);
        if (Remove(hwnd))
        {
            WindowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != _shellHookMessage || _shellHookMessage == 0)
        {
            return;
        }

        var hwnd = lParam;
        switch ((int)wParam)
        {
            case HSHELL_WINDOWCREATED:
                TryAdd(hwnd, raise: true);
                break;

            case HSHELL_WINDOWDESTROYED:
                if (Remove(hwnd))
                {
                    if (ActiveWindow == hwnd)
                    {
                        ActiveWindow = 0;
                    }

                    WindowsChanged?.Invoke(this, EventArgs.Empty);
                }

                break;

            case HSHELL_WINDOWACTIVATED:
            case HSHELL_RUDEAPPACTIVATED:
                // Activation is also where previously cloaked windows (UWP) first become eligible.
                TryAdd(hwnd, raise: false);
                StopFlashing(hwnd);
                ActiveWindow = ResolveActiveWindow(ActiveWindow, hwnd, IndexOf(hwnd) >= 0, GetForegroundWindow(), IsOwnWindow, IsIconic);

                WindowsChanged?.Invoke(this, EventArgs.Empty);
                break;

            case HSHELL_REDRAW:
                Refresh(hwnd);
                break;

            case HSHELL_FLASH:
                Refresh(hwnd);
                OnFlash(hwnd);
                break;

            case HSHELL_WINDOWREPLACED:
                Remove(hwnd);
                WindowsChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Decides which tracked window counts as active after the shell reports an activation.
    /// The shell reports 0 for windows without a taskbar button, which includes SuperBar's own bar: clicking the
    /// bar must not clear the active window, or an app button could never minimize the window it just showed.
    /// </summary>
    internal static nint ResolveActiveWindow(nint current, nint activated, bool activatedIsTracked, nint foreground, Func<nint, bool> isOwnWindow, Func<nint, bool> isMinimized)
    {
        if (activatedIsTracked)
        {
            return activated;
        }

        // Foreground 0 is a transient state while activation is still moving; treat it like our own surface.
        var subject = activated != 0 ? activated : foreground;
        if (subject == 0 || isOwnWindow(subject))
        {
            return current != 0 && isMinimized(current) ? 0 : current;
        }

        // Desktop, Start, another shell surface: nothing on the bar is active.
        return 0;
    }

    private bool IsOwnWindow(nint hwnd)
    {
        if (_ignored.Contains(hwnd))
        {
            return true;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void Refresh(nint hwnd)
    {
        var index = IndexOf(hwnd);
        if (index < 0)
        {
            TryAdd(hwnd, raise: true);
            return;
        }

        if (!IsWindow(hwnd) || !IsEligible(hwnd))
        {
            _windows.RemoveAt(index);
            WindowsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var title = GetTitle(hwnd);
        if (title != _windows[index].Title)
        {
            _windows[index] = _windows[index] with { Title = title };
            WindowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TryAdd(nint hwnd, bool raise)
    {
        if (hwnd == 0 || _ignored.Contains(hwnd) || IndexOf(hwnd) >= 0 || !IsEligible(hwnd))
        {
            return;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        _windows.Add(new TrackedWindow(hwnd, GetTitle(hwnd), (int)pid, GetProcessPath((int)pid)));
        if (raise)
        {
            WindowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Remove(nint hwnd)
    {
        var index = IndexOf(hwnd);
        if (index < 0)
        {
            return false;
        }

        _windows.RemoveAt(index);
        return true;
    }

    private int IndexOf(nint hwnd) => _windows.FindIndex(w => w.Handle == hwnd);

    internal static bool IsEligible(nint hwnd)
    {
        var traits = new WindowTraits(
            IsWindowVisible(hwnd),
            IsCloaked(hwnd),
            GetWindow(hwnd, GW_OWNER) != 0,
            GetWindowLongPtr(hwnd, GWL_EXSTYLE),
            GetWindowTextLength(hwnd),
            GetClass(hwnd));
        return WindowFilter.IsTaskbarCandidate(traits);
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string GetTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        var copied = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, copied);
    }

    private static string GetClass(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private string? GetProcessPath(int pid)
    {
        if (_processPaths.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string? path = null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process != 0)
        {
            try
            {
                var buffer = new char[1024];
                var size = (uint)buffer.Length;
                if (QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    path = new string(buffer, 0, (int)size);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // PIDs are recycled; keep the cache small and short-lived.
        if (_processPaths.Count > 512)
        {
            _processPaths.Clear();
        }

        _processPaths[pid] = path;
        return path;
    }

    public void Dispose()
    {
        if (_started)
        {
            _shell.MessageReceived -= OnMessage;
            _taskbarList.OverlayIconChanged -= OnOverlayIconChanged;
            _taskbarList.Claimed -= OnTaskbarListClaimed;
            _taskbarList.Dispose();
            DeregisterShellHookWindow(_shell.Handle);
        }
    }
}
