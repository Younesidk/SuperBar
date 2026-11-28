using System.Runtime.InteropServices;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Apps;

/// <summary>
/// Icons rendered by the shell itself (IShellItemImageFactory), as Explorer's taskbar shows them: a shortcut's own icon,
/// a packaged app's logo on a transparent background, at the exact pixel size asked for. Call on an STA thread.
/// </summary>
internal static class ShellImages
{
    public static IconPixels? FromParsingName(string parsingName, int sizePx)
    {
        if (ShellInterop.SHCreateItemFromParsingName(parsingName, 0, ShellInterop.IID_IShellItemImageFactory, out var item) != 0)
        {
            return null;
        }

        try
        {
            return Render((IShellItemImageFactory)item, sizePx);
        }
        finally
        {
            ShellInterop.Release(item);
        }
    }

    public static IconPixels? FromIdList(nint pidl, int sizePx)
    {
        if (ShellInterop.SHCreateItemFromIDList(pidl, ShellInterop.IID_IShellItemImageFactory, out var item) != 0)
        {
            return null;
        }

        try
        {
            return Render((IShellItemImageFactory)item, sizePx);
        }
        finally
        {
            ShellInterop.Release(item);
        }
    }

    private static IconPixels? Render(IShellItemImageFactory factory, int sizePx)
    {
        // ICONONLY: the app's icon, never a thumbnail of the file's contents.
        if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, ShellInterop.SIIGBF_ICONONLY, out var bitmap) != 0 || bitmap == 0)
        {
            return null;
        }

        try
        {
            return FromHBitmap(bitmap);
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    /// <summary>
    /// Reads a 32-bit shell bitmap. Its alpha is straight (measured: colour channels above alpha are common, which
    /// premultiplied data cannot have), the same as <see cref="IconPixels"/>, so the pixels are used as they are.
    /// </summary>
    private static IconPixels? FromHBitmap(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
        {
            return null;
        }

        var width = bmp.bmWidth;
        var height = Math.Abs(bmp.bmHeight);
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };

        var dc = GetDC(0);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(0, dc);
        }

        if (!HasAlpha(pixels))
        {
            // No alpha at all (a 24-bit image in a 32-bit bitmap): treat it as opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new IconPixels(width, height, pixels);
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }
}
