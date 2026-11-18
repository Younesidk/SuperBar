using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Apps;

internal sealed class AppLauncher(ILogger<AppLauncher> logger) : IAppLauncher
{
    public bool Launch(string path, string? arguments = null)
    {
        try
        {
            // UseShellExecute handles .exe, .lnk, shell: URIs and UAC elevation prompts like Explorer does.
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = arguments ?? "",
            };

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                info.WorkingDirectory = directory;
            }

            Process.Start(info)?.Dispose();
            logger.LogInformation("Launched {Path}", path);
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
}

/// <summary>Opens built-in shell surfaces by sending their system shortcuts (they are owned by Explorer / ShellExperienceHost).</summary>
internal sealed class ShellCommands(ILogger<ShellCommands> logger) : IShellCommands
{
    private const ushort VK_N = 0x4E;
    private const ushort VK_A = 0x41;

    public void OpenStartMenu() => SendChord(VK_LWIN);

    public void OpenNotificationCenter() => SendChord(VK_LWIN, VK_N);

    public void OpenQuickSettings() => SendChord(VK_LWIN, VK_A);

    public void OpenTaskManager() => SendChord(VK_CONTROL, VK_SHIFT, VK_ESCAPE);

    private void SendChord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], up: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            logger.LogWarning("SendInput sent {Sent}/{Total} events", sent, inputs.Length);
        }
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };
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
