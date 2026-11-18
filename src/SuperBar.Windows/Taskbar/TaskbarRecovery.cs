using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Taskbar;

/// <summary>
/// Thread-agnostic, dependency-free restore of the native taskbar from a recovery record.
/// Used by normal shutdown, crash handlers and the `--restore-taskbar` command line switch.
/// </summary>
internal static class TaskbarRecovery
{
    public const string PrimaryTaskbarClass = "Shell_TrayWnd";

    public static bool Restore(ITaskbarRecoveryStore store, ILogger logger)
    {
        var record = store.Load();
        if (record is null)
        {
            return false;
        }

        RestoreFrom(record, store, logger);
        return true;
    }

    public static void RestoreFrom(NativeTaskbarRecoveryRecord? record, ITaskbarRecoveryStore store, ILogger logger)
    {
        var taskbar = FindWindow(PrimaryTaskbarClass, null);
        if (taskbar == 0)
        {
            // Explorer is not running; it shows its taskbar itself when it starts. Keep the record
            // while auto-hide still needs reverting so the next launch can finish the job.
            logger.LogWarning("Native taskbar window not found during restore");
            if (record is not { AutoHideChangedBySuperBar: true })
            {
                store.Clear();
            }

            return;
        }

        if (record is { AutoHideChangedBySuperBar: true })
        {
            SetState(taskbar, record.OriginalAppBarState & ~ABS_AUTOHIDE);
        }

        ShowWindow(taskbar, SW_SHOWNOACTIVATE);
        store.Clear();
        logger.LogInformation("Native taskbar restored");
    }

    public static int GetState(nint taskbar)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    public static void SetState(nint taskbar, int state)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar, lParam = state };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }
}
