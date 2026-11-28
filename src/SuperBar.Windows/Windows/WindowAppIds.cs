using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Windows;

/// <summary>
/// A window's explicit AppUserModelID, which the Windows taskbar groups by: the window's own (set through its property
/// store by browsers for profiles and web apps), else its package's. UWP apps run inside ApplicationFrameHost.exe, so
/// their package is the one of the app's CoreWindow hosted in the frame. Call on the UI thread.
/// </summary>
internal sealed class WindowAppIds
{
    private const string HostedCoreWindowClass = "Windows.UI.Core.CoreWindow";
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    // Packages by process id (PIDs are recycled: kept small, like the process path cache).
    private readonly Dictionary<int, string?> _packages = new();

    public (string? AppId, bool IsPackaged) Get(nint hwnd, int processId, string? processPath)
    {
        var windowId = FromWindow(hwnd);
        var packageId = FromProcess(processId);
        if (packageId is null && processPath is not null && processPath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            packageId = FromHostedCoreWindow(hwnd);
        }

        var appId = windowId ?? packageId;
        return (appId, packageId is not null || PinMatching.IsPackagedAppId(appId));
    }

    private static string? FromWindow(nint hwnd)
    {
        if (ShellInterop.SHGetPropertyStoreForWindow(hwnd, ShellInterop.IID_IPropertyStore, out var store) != 0)
        {
            return null;
        }

        try
        {
            return ShellInterop.GetString(store, ShellInterop.PKEY_AppUserModel_ID);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ShellInterop.Release(store);
        }
    }

    private string? FromProcess(int processId)
    {
        if (_packages.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        string? id = null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process != 0)
        {
            try
            {
                uint length = 0;
                if (ShellInterop.GetApplicationUserModelId(process, ref length, null) == ERROR_INSUFFICIENT_BUFFER && length > 0)
                {
                    var buffer = new char[length];
                    if (ShellInterop.GetApplicationUserModelId(process, ref length, buffer) == ShellInterop.ERROR_SUCCESS)
                    {
                        id = new string(buffer, 0, (int)Math.Max(0, length - 1));
                    }
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        if (_packages.Count > 512)
        {
            _packages.Clear();
        }

        _packages[processId] = id;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private string? FromHostedCoreWindow(nint frame)
    {
        // Not cached by process: one ApplicationFrameHost hosts every UWP app.
        var child = ShellInterop.FindWindowEx(frame, 0, HostedCoreWindowClass, null);
        if (child == 0 || GetWindowThreadProcessId(child, out var pid) == 0)
        {
            return null;
        }

        return FromProcess((int)pid);
    }
}
