using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using SuperBar.Windows.Interop;
using static SuperBar.Windows.Interop.NativeMethods;

namespace SuperBar.Windows.Apps;

/// <summary>Extracts real icons from windows and files and converts HICONs into framework-neutral BGRA pixels.</summary>
internal sealed class IconProvider(ILogger<IconProvider> logger) : IIconProvider
{
    public IconPixels? GetWindowIcon(nint windowHandle)
    {
        // WM_GETICON icons are owned by the window: convert, never destroy.
        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeout(windowHandle, WM_GETICON, kind, 0, SMTO_ABORTIFHUNG, 50, out var hIcon) != 0 && hIcon != 0)
            {
                return FromHIcon(hIcon);
            }
        }

        var classIcon = GetClassLongPtr(windowHandle, GCLP_HICON);
        if (classIcon == 0)
        {
            classIcon = GetClassLongPtr(windowHandle, GCLP_HICONSM);
        }

        return classIcon != 0 ? FromHIcon(classIcon) : null;
    }

    public IconPixels? GetFileIcon(string path, int sizePx)
    {
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out var large, out var small, (uint)sizePx | (16u << 16)) == 0 && large != 0)
            {
                try
                {
                    return FromHIcon(large);
                }
                finally
                {
                    DestroyIcon(large);
                    if (small != 0)
                    {
                        DestroyIcon(small);
                    }
                }
            }

            var info = new SHFILEINFO();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != 0 && info.hIcon != 0)
            {
                try
                {
                    return FromHIcon(info.hIcon);
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Icon extraction failed for {Path}", path);
        }

        return null;
    }

    public IconPixels? GetShellItemIcon(string parsingName, int sizePx)
    {
        try
        {
            return ShellImages.FromParsingName(parsingName, sizePx);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Shell icon failed for {Item}", parsingName);
            return null;
        }
    }

    internal static IconPixels? FromHIcon(nint hIcon)
    {
        if (!GetIconInfo(hIcon, out var info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor == 0 || GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
            {
                return null;
            }

            var width = bmp.bmWidth;
            var height = bmp.bmHeight;
            if (width <= 0 || height <= 0 || width > 512 || height > 512)
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            var dc = GetDC(0);
            try
            {
                var header = TopDown32(width, height);
                if (GetDIBits(dc, info.hbmColor, 0, (uint)height, pixels, ref header, 0) == 0)
                {
                    return null;
                }

                if (!HasAlpha(pixels))
                {
                    // Legacy icon: derive transparency from the AND mask (white = transparent).
                    var mask = new byte[pixels.Length];
                    var maskHeader = TopDown32(width, height);
                    var haveMask = info.hbmMask != 0 && GetDIBits(dc, info.hbmMask, 0, (uint)height, mask, ref maskHeader, 0) != 0;
                    for (var i = 3; i < pixels.Length; i += 4)
                    {
                        pixels[i] = haveMask && mask[i - 1] != 0 ? (byte)0 : (byte)255;
                    }
                }
            }
            finally
            {
                ReleaseDC(0, dc);
            }

            return new IconPixels(width, height, pixels);
        }
        finally
        {
            if (info.hbmColor != 0)
            {
                DeleteObject(info.hbmColor);
            }

            if (info.hbmMask != 0)
            {
                DeleteObject(info.hbmMask);
            }
        }
    }

    private static BITMAPINFOHEADER TopDown32(int width, int height) => new()
    {
        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = width,
        biHeight = -height,
        biPlanes = 1,
        biBitCount = 32,
    };

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
