using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SuperBar.Core.Shell;

namespace SuperBar.Runtime.Services;

/// <summary>Converts shell icons to Avalonia bitmaps once and keeps them for reuse.</summary>
public sealed class IconCache(IIconProvider provider) : IDisposable
{
    private const int IconPixelSize = 48; // crisp at up to 150% scaling for a 32 DIP icon
    private readonly Dictionary<string, Bitmap?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, Bitmap?> _byWindow = new();

    public Bitmap? ForFile(string path)
    {
        if (!_byPath.TryGetValue(path, out var bitmap))
        {
            bitmap = ToBitmap(provider.GetFileIcon(path, IconPixelSize));
            _byPath[path] = bitmap;
        }

        return bitmap;
    }

    public Bitmap? ForWindow(nint hwnd)
    {
        if (!_byWindow.TryGetValue(hwnd, out var bitmap))
        {
            if (_byWindow.Count > 256)
            {
                _byWindow.Clear();
            }

            bitmap = ToBitmap(provider.GetWindowIcon(hwnd));
            _byWindow[hwnd] = bitmap;
        }

        return bitmap;
    }

    public static Bitmap? ToBitmap(IconPixels? pixels)
    {
        if (pixels is null)
        {
            return null;
        }

        var handle = GCHandle.Alloc(pixels.Bgra, GCHandleType.Pinned);
        try
        {
            // The Bitmap copies the buffer, so the pin only needs to last for the constructor.
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(),
                new PixelSize(pixels.Width, pixels.Height),
                new Vector(96, 96),
                pixels.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        foreach (var b in _byPath.Values.Concat(_byWindow.Values))
        {
            b?.Dispose();
        }

        _byPath.Clear();
        _byWindow.Clear();
    }
}
