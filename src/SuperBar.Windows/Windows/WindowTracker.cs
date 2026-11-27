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
    private readonly IBadgeStore _badgeStore;

    // Badges by window, shown or not: an app may badge a window hidden in the tray, and the badge must be there
    // when the window comes back. Saved so a restarted bar shows them (apps only re-send a badge when it changes).
    private readonly Dictionary<nint, SavedBadge> _badges = new();
    private readonly Lock _saveLock = new();
    private IReadOnlyList<SavedBadge>? _pendingSave;
    private Timer? _saveTimer;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private uint _shellHookMessage;
    private bool _started;

    public WindowTracker(ShellMessageWindow shell, TaskbarListHost taskbarList, IBadgeStore badgeStore, ILogger<WindowTracker> logger)
    {
        _shell = shell;
        _taskbarList = taskbarList;
        _badgeStore = badgeStore;
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
        RestoreBadges();

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
        if (icon is null)
        {
            _badges.Remove(hwnd);
        }
        else
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            _badges[hwnd] = new SavedBadge(hwnd, (int)pid, icon);
        }

        ScheduleSave();

        var index = IndexOf(hwnd);
        if (index < 0)
        {
            return;
        }

        _windows[index] = _windows[index] with { OverlayIcon = icon };
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes back the badges saved by the previous run, for the windows that still exist.</summary>
    private void RestoreBadges()
    {
        foreach (var badge in _badgeStore.Load())
        {
            var hwnd = (nint)badge.Window;
            // A handle can be reused by another process once its window is gone: check the owner too.
            if (IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0 && (int)pid == badge.ProcessId)
            {
                _badges[hwnd] = badge;
            }
        }

        if (_badges.Count > 0)
        {
            _logger.LogInformation("Restored {Count} app badge(s) from the previous run", _badges.Count);
        }
    }

    /// <summary>Saves the badges shortly after a change (badges can change several times in a row).</summary>
    private void ScheduleSave()
    {
        var snapshot = CurrentBadges();
        lock (_saveLock)
        {
            _pendingSave = snapshot;
            _saveTimer ??= new Timer(_ => SavePending());
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private List<SavedBadge> CurrentBadges()
    {
        foreach (var gone in _badges.Keys.Where(h => !IsWindow(h)).ToList())
        {
            _badges.Remove(gone);
        }

        return _badges.Values.ToList();
    }

    private void SavePending()
    {
        lock (_saveLock)
        {
            if (_pendingSave is not { } badges)
            {
                return;
            }

            _pendingSave = null;
            try
            {
                _badgeStore.Save(badges);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save app badges");
            }
        }
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
        _badges.TryGetValue(hwnd, out var badge);
        _windows.Add(new TrackedWindow(hwnd, GetTitle(hwnd), (int)pid, GetProcessPath((int)pid), OverlayIcon: badge?.Icon));
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

            // Write the latest badges now rather than on the timer: the process is about to end.
            lock (_saveLock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
                _pendingSave = CurrentBadges();
            }

            SavePending();
        }
    }
}
