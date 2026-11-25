using System.Runtime.InteropServices;
using SuperBar.Core.Geometry;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Monitors;

internal sealed class MonitorService : IMonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint hMonitor, nint _, ref RECT _, nint _) =>
        {
            if (TryGet(hMonitor) is { } info)
            {
                result.Add(info);
            }

            return true;
        }, 0);
        return result;
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors.FirstOrDefault()
               ?? throw new InvalidOperationException("No display monitors found");
    }

    internal static MonitorInfo? TryGet(nint hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
        {
            return null;
        }

        var scale = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return new MonitorInfo(
            hMonitor,
            mi.szDevice,
            ToRect(mi.rcMonitor),
            ToRect(mi.rcWork),
            scale,
            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    internal static ScreenRect ToRect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
