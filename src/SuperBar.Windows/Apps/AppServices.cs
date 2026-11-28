using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Apps;

internal sealed class AppLauncher(ILogger<AppLauncher> logger) : IAppLauncher
{
    public bool Launch(string path, string? arguments = null) => Start(path, arguments, workingDirectory: null);

    public bool Launch(AppLaunchTarget target)
    {
        // The pin's own shortcut carries everything the Windows taskbar would use: arguments, working directory,
        // AppUserModelID (so the new window groups with its pin), "Run as administrator".
        if (!string.IsNullOrEmpty(target.ShortcutPath) && File.Exists(target.ShortcutPath))
        {
            return Start(target.ShortcutPath, null, null, setWorkingDirectory: false);
        }

        if (!string.IsNullOrEmpty(target.PackagedAppId) && ActivatePackagedApp(target.PackagedAppId, target.Arguments))
        {
            return true;
        }

        if (PinMatching.AppsFolderId(target.Path) is { } appId)
        {
            // Explorer resolves any app of the Start menu's list by id, packaged or not.
            return Start("explorer.exe", PinMatching.AppsFolderPath(appId), null, setWorkingDirectory: false);
        }

        return Start(target.Path, target.Arguments, target.WorkingDirectory);
    }

    private bool ActivatePackagedApp(string appId, string? arguments)
    {
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellInterop.CLSID_ApplicationActivationManager, throwOnError: true)!);
            var hr = ((IApplicationActivationManager)manager!).ActivateApplication(appId, arguments, ShellInterop.AO_NONE, out var processId);
            if (hr == 0)
            {
                logger.LogInformation("Activated packaged app {AppId} (process {ProcessId})", appId, processId);
                return true;
            }

            logger.LogInformation("Activating packaged app {AppId} failed (0x{Result:X8}); starting it through Explorer", appId, hr);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Activating packaged app {AppId} failed; starting it through Explorer", appId);
        }
        finally
        {
            ShellInterop.Release(manager);
        }

        return false;
    }

    private bool Start(string path, string? arguments, string? workingDirectory, bool setWorkingDirectory = true)
    {
        try
        {
            // UseShellExecute handles .exe, .lnk, shell: URIs and UAC elevation prompts like Explorer does.
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
            };

            var directory = !string.IsNullOrEmpty(workingDirectory) ? workingDirectory : Path.GetDirectoryName(path);
            if (setWorkingDirectory && !string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                info.WorkingDirectory = directory;
            }

            Process.Start(info)?.Dispose();
            logger.LogInformation("Launched {Path} {Arguments}", path, arguments ?? "");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            logger.LogInformation("Launch of {Path} cancelled by the user (UAC)", path);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to launch {Path}", path);
            return false;
        }
    }
}

internal sealed class WindowSwitcher(ILogger<WindowSwitcher> logger) : IWindowSwitcher
{
    public void ActivateOrMinimize(nint windowHandle, bool isActive)
    {
        if (isActive && !IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, SW_MINIMIZE);
            return;
        }

        Activate(windowHandle);
    }

    public void Activate(nint windowHandle)
    {
        if (!IsWindow(windowHandle))
        {
            return;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }

        // Allowed because the user just clicked SuperBar, which makes it the foreground process.
        if (!SetForegroundWindow(windowHandle))
        {
            logger.LogDebug("SetForegroundWindow refused for 0x{Handle:X}", windowHandle);
        }
    }

    public void Close(nint windowHandle)
    {
        // SC_CLOSE rather than WM_CLOSE: it is what the system menu and the Windows taskbar send.
        if (IsWindow(windowHandle) && !PostMessage(windowHandle, WM_SYSCOMMAND, SC_CLOSE, 0))
        {
            logger.LogDebug("Could not ask window 0x{Handle:X} to close", windowHandle);
        }
    }
}

internal sealed class SystemMetricsProvider : ISystemMetricsProvider
{
    private long _lastIdle;
    private long _lastTotal;

    public SystemResourceSnapshot Sample()
    {
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel time includes idle time
            var totalDelta = total - _lastTotal;
            var idleDelta = idle - _lastIdle;
            if (_lastTotal != 0 && totalDelta > 0)
            {
                cpu = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }

            _lastTotal = total;
            _lastIdle = idle;
        }

        var memory = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory) || memory.ullTotalPhys == 0)
        {
            return new SystemResourceSnapshot(cpu, 0, 0, 0);
        }

        var used = memory.ullTotalPhys - memory.ullAvailPhys;
        return new SystemResourceSnapshot(cpu, 100.0 * used / memory.ullTotalPhys, used, memory.ullTotalPhys);
    }
}
