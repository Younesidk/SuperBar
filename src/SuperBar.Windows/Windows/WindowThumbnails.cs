using Microsoft.Extensions.Logging;
using SuperBar.Core.Geometry;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Windows;

/// <summary>DWM live thumbnails (DwmRegisterThumbnail), the mechanism behind the Windows taskbar's previews.</summary>
internal sealed class WindowThumbnailService(ILogger<WindowThumbnailService> logger) : IWindowThumbnailService
{
    public IWindowThumbnail? Create(nint destinationWindow, nint sourceWindow)
    {
        if (!IsWindow(destinationWindow) || !IsWindow(sourceWindow))
        {
            return null;
        }

        var hr = DwmRegisterThumbnail(destinationWindow, sourceWindow, out var thumbnail);
        if (hr < 0 || thumbnail == 0)
        {
            logger.LogDebug("DwmRegisterThumbnail failed for window 0x{Handle:X} (0x{Hr:X8})", sourceWindow, hr);
            return null;
        }

        return new WindowThumbnail(thumbnail);
    }
}

internal sealed class CursorService : ICursorService
{
    public bool TryGetPosition(out int x, out int y)
    {
        var ok = GetCursorPos(out var p);
        (x, y) = (p.X, p.Y);
        return ok;
    }
}

internal sealed class WindowThumbnail(nint handle) : IWindowThumbnail
{
    private nint _handle = handle;

    public PixelSize SourceSize =>
        _handle != 0 && DwmQueryThumbnailSourceSize(_handle, out var size) >= 0 ? new PixelSize(size.cx, size.cy) : default;

    public void Show(ScreenRect destination)
    {
        if (_handle == 0)
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT(destination.Left, destination.Top, destination.Right, destination.Bottom),
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_handle, ref properties);
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            DwmUnregisterThumbnail(_handle);
            _handle = 0;
        }
    }
}
