using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SuperBar.Core.Imaging;
using SuperBar.Core.Shell;

namespace SuperBar.Runtime.Services;

/// <summary>
/// Converts shell icons to WinUI bitmaps once and keeps them for reuse. Synchronous on purpose so
/// <c>AppsSectionViewModel.Rebuild</c> stays synchronous and race-free. UI thread only.
/// </summary>
public sealed class IconCache(IIconProvider provider) : IDisposable
{
    private const int IconPixelSize = 48; // crisp at up to 150% scaling for a 32 DIP icon
    private const int MaxWindowIcons = 256;
    private readonly Dictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ImageSource?> _byWindow = new();

    public ImageSource? ForFile(string path)
    {
        if (!_byPath.TryGetValue(path, out var bitmap))
        {
            bitmap = ToBitmap(provider.GetFileIcon(path, IconPixelSize));
            _byPath[path] = bitmap;
        }

        return bitmap;
    }

    public ImageSource? ForWindow(nint hwnd)
    {
        if (!_byWindow.TryGetValue(hwnd, out var bitmap))
        {
            if (_byWindow.Count >= MaxWindowIcons)
            {
                _byWindow.Clear();
            }

            bitmap = ToBitmap(provider.GetWindowIcon(hwnd));
            _byWindow[hwnd] = bitmap;
        }

        return bitmap;
    }

    /// <summary>
    /// <see cref="IIconProvider"/> yields straight-alpha BGRA, but <see cref="WriteableBitmap"/> expects premultiplied
    /// BGRA. Copying straight alpha as-is draws dark fringes around anti-aliased icon edges, so premultiply here.
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

    public void Dispose()
    {
        _byPath.Clear();
        _byWindow.Clear();
    }
}
