using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using SuperBar.Windows.Shell;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Taskbar;

/// <summary>
/// Hides the native (primary-monitor) Windows taskbar while SuperBar runs, without terminating Explorer.
///
/// Strategy (same family of techniques used by established shell replacements):
///  1. Record the user's original taskbar state in a recovery file BEFORE touching anything.
///  2. Temporarily switch the native taskbar to auto-hide via ABM_SETSTATE so it stops reserving
///     work-area space (a merely hidden taskbar keeps its reservation and SuperBar would float above a gap).
///     This is the official shell API; Explorer persists it, which is why it is always reverted.
///  3. Hide Shell_TrayWnd with ShowWindow(SW_HIDE) - a non-persistent change that Explorer undoes on restart.
///  4. Watch EVENT_OBJECT_SHOW for the taskbar window (event-driven, no polling) and re-hide it when
///     Explorer shows it again (auto-hide reveal, Win key, etc.).
///  5. On exit, on crash handlers, or on the next launch (recovery file present) restore the original
///     auto-hide state and show the taskbar.
///
/// Limitations: secondary-monitor taskbars are not hidden (they follow the auto-hide setting while SuperBar
/// runs). A hard kill (e.g. Task Manager "End task" on a hung process) cannot run cleanup; the taskbar then
/// stays hidden until SuperBar starts again, `SuperBar.exe --restore-taskbar` is run, or Explorer restarts
/// (auto-hide stays enabled in that last case until SuperBar's recovery runs).
/// </summary>
internal sealed class NativeTaskbarController : INativeTaskbarController, IDisposable
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";

    private readonly ITaskbarRecoveryStore _recovery;
    private readonly ILogger<NativeTaskbarController> _logger;
    private readonly TaskbarListHost _taskbarList;
    private readonly Lock _gate = new();
    private readonly WinEventProc _showHookProc;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly uint _shellHookMessage = RegisterWindowMessage("SHELLHOOK");
    private nint _showHook;
    private nint _hookedTaskbar;

    public NativeTaskbarController(ITaskbarRecoveryStore recovery, ShellMessageWindow shell, TaskbarListHost taskbarList, ILogger<NativeTaskbarController> logger)
    {
        _recovery = recovery;
        _taskbarList = taskbarList;
        _logger = logger;
        _showHookProc = OnWindowShown;
        shell.ExplorerRestarted += OnExplorerRestarted;
        shell.MessageReceived += OnShellMessage;
    }

    public NativeTaskbarState State { get; private set; } = NativeTaskbarState.Visible;

    public void Hide()
    {
        lock (_gate)
        {
            if (State == NativeTaskbarState.Hidden)
            {
                return;
            }

            var taskbar = FindWindow(PrimaryTaskbarClass, null);
            if (taskbar == 0)
            {
                _logger.LogWarning("Native taskbar window not found (Explorer not running?); nothing to hide");
                return;
            }

            var original = GetState(taskbar);
            var changeAutoHide = (original & ABS_AUTOHIDE) == 0;

            // 1. Persist before mutating, so a crash at any later point is recoverable.
            _recovery.Save(new NativeTaskbarRecoveryRecord(true, changeAutoHide, original, Environment.ProcessId, DateTimeOffset.UtcNow));

            // 2. Free the work area.
            if (changeAutoHide)
            {
                SetState(taskbar, original | ABS_AUTOHIDE);
            }

            // 3. Keep it hidden. The hook goes in first so a show that races the hide below is still caught.
            InstallShowHook(taskbar);

            // 4. Hide.
            ShowWindow(taskbar, SW_HIDE);

            // 6. Take over the app requests meant for its buttons (badges).
            _taskbarList.Claim();

            State = NativeTaskbarState.Hidden;
            _logger.LogInformation("Native taskbar hidden (original state 0x{State:X}, auto-hide changed: {Changed})", original, changeAutoHide);
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            var record = _recovery.Load();
            if (State == NativeTaskbarState.Visible && record is null)
            {
                return;
            }

            RemoveShowHook();
            _taskbarList.Release();
            RestoreFrom(record);
            State = NativeTaskbarState.Visible;
        }
    }

    public bool RecoverFromPreviousSession()
    {
        lock (_gate)
        {
            var record = _recovery.Load();
            if (record is null)
            {
                return false;
            }

            _logger.LogWarning("Found taskbar recovery record from a previous session (pid {Pid}, {When}); restoring native taskbar", record.ProcessId, record.ChangedAtUtc);
            _taskbarList.Release();
            RestoreFrom(record);
            State = NativeTaskbarState.Visible;
            return true;
        }
    }

    private void RestoreFrom(NativeTaskbarRecoveryRecord? record) => TaskbarRecovery.RestoreFrom(record, _recovery, _logger);

    private void OnExplorerRestarted(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (State != NativeTaskbarState.Hidden)
            {
                return;
            }

            // Explorer re-reads the (still auto-hide) setting; only the window visibility and our hook are lost.
            var taskbar = FindWindow(PrimaryTaskbarClass, null);
            if (taskbar == 0)
            {
                return;
            }

            InstallShowHook(taskbar);
            ShowWindow(taskbar, SW_HIDE);
            _taskbarList.Claim();
            _logger.LogInformation("Re-hid native taskbar after Explorer restart");
        }
    }

    /// <summary>
    /// Safety net for the show hook: Explorer has been seen to leave its taskbar visible after a quick
    /// exit-and-relaunch without the hook catching it, after which every Win key press revealed it. Each window
    /// activation reported by the shell hook (opening Start is one) re-checks and hides it again. Event-driven;
    /// the check is one IsWindowVisible call.
    /// </summary>
    private void OnShellMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != _shellHookMessage || _shellHookMessage == 0 || State != NativeTaskbarState.Hidden)
        {
            return;
        }

        var code = (int)wParam;
        if (code != HSHELL_WINDOWACTIVATED && code != HSHELL_RUDEAPPACTIVATED)
        {
            return;
        }

        lock (_gate)
        {
            var taskbar = FindWindow(PrimaryTaskbarClass, null);
            if (State != NativeTaskbarState.Hidden || taskbar == 0 || !IsWindowVisible(taskbar))
            {
                return;
            }

            if (taskbar != _hookedTaskbar)
            {
                InstallShowHook(taskbar);
            }

            ShowWindow(taskbar, SW_HIDE);
            _logger.LogInformation("Native taskbar was visible again; hid it");
        }
    }

    private void InstallShowHook(nint taskbar)
    {
        RemoveShowHook();
        var thread = GetWindowThreadProcessId(taskbar, out var pid);
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, _showHookProc, pid, thread, WINEVENT_OUTOFCONTEXT);
        _hookedTaskbar = taskbar;
        if (_showHook == 0)
        {
            _logger.LogWarning("Could not install taskbar visibility hook; the native taskbar may reappear");
        }
    }

    private void RemoveShowHook()
    {
        // UnhookWinEvent must run on the installing thread; crash-time restores from other threads skip it
        // (the hook dies with the process anyway).
        if (_showHook != 0 && Environment.CurrentManagedThreadId == _uiThreadId)
        {
            UnhookWinEvent(_showHook);
        }

        _showHook = 0;
        _hookedTaskbar = 0;
    }

    private void OnWindowShown(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        const int OBJID_WINDOW = 0;
        if (hwnd == _hookedTaskbar && idObject == OBJID_WINDOW && State == NativeTaskbarState.Hidden)
        {
            ShowWindow(hwnd, SW_HIDE);
        }
    }

    private static int GetState(nint taskbar) => TaskbarRecovery.GetState(taskbar);

    private static void SetState(nint taskbar, int state) => TaskbarRecovery.SetState(taskbar, state);

    public void Dispose() => Restore();
}
