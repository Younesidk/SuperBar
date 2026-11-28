using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using SuperBar.Core.Imaging;
using SuperBar.Core.Shell;

namespace SuperBar.ControlCenter.ViewModels;

/// <summary>Turns shell icon pixels into WinUI bitmaps (the Control Center has no access to the bar's icon cache).</summary>
internal static class IconImages
{
    /// <summary>
    /// <see cref="IIconProvider"/> yields straight-alpha BGRA; <see cref="WriteableBitmap"/> expects premultiplied BGRA,
    /// otherwise anti-aliased icon edges get dark fringes.
    /// </summary>
    public static WriteableBitmap? ToBitmap(IconPixels? pixels)
    {
        if (pixels is null || pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length < pixels.Width * pixels.Height * 4)
        {
            return null;
        }

        var premultiplied = BgraPixels.Premultiply(pixels.Bgra.AsSpan(0, pixels.Width * pixels.Height * 4));
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(premultiplied, 0, premultiplied.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
