using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SuperBar.Core.Imaging;
using SuperBar.Core.Layout;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Runtime.Services;

/// <summary>
/// Converts shell icons to WinUI bitmaps once and keeps them for reuse. Synchronous on purpose so
/// <c>AppsSectionViewModel.Rebuild</c> stays synchronous and race-free. UI thread only.
/// </summary>
public sealed class IconCache(IIconProvider provider, IPinIconStore pinIcons) : IDisposable
{
    private const int IconPixelSize = 48; // crisp at up to 150% scaling for a 32 DIP icon

    /// <summary>Pins synced from Windows are rendered by the shell at this size: crisp at 200% for a 32 DIP icon.</summary>
    public const int PinIconPixelSize = 64;

    private const int MaxWindowIcons = 256;
    private readonly Dictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _byPin = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _byShellItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ImageSource?> _byWindow = new();
    private readonly ConditionalWeakTable<IconPixels, ImageSource> _byPixels = new();

    /// <summary>A bitmap for icon pixels the shell already extracted (badges); converted once per pixel buffer.</summary>
    public ImageSource? ForPixels(IconPixels? pixels)
    {
        if (pixels is null)
        {
            return null;
        }

        if (!_byPixels.TryGetValue(pixels, out var bitmap))
        {
            bitmap = ToBitmap(pixels);
            if (bitmap is null)
            {
                return null;
            }

            _byPixels.AddOrUpdate(pixels, bitmap);
        }

        return bitmap;
    }

    public ImageSource? ForFile(string path)
    {
        if (!_byPath.TryGetValue(path, out var bitmap))
        {
            bitmap = ToBitmap(provider.GetFileIcon(path, IconPixelSize));
            _byPath[path] = bitmap;
        }

        return bitmap;
    }

    /// <summary>
    /// A pin's icon. Pins synced from Windows use the icon the shell rendered for them (the shortcut's own icon, a
    /// packaged app's logo), saved on disk by the last sync so it shows at once on start-up; until a sync has rendered
    /// one, the executable's icon stands in. Other pins use their executable's icon, or a packaged app's logo.
    /// </summary>
    public ImageSource? ForPin(PinnedApp pin)
    {
        var key = AppOrdering.PinKey(pin);
        if (_byPin.TryGetValue(key, out var bitmap))
        {
            return bitmap;
        }

        bitmap = pin.PinnedInWindows ? ToBitmap(pinIcons.Load(key)) : null;
        if (bitmap is null)
        {
            bitmap = PinMatching.AppsFolderId(pin.Path) is not null
                ? (pin.PinnedInWindows ? null : ForShellItem(pin.Path)) // a sync renders the logo off the UI thread
                : ForFile(pin.Path);
        }

        _byPin[key] = bitmap;
        return bitmap;
    }

    /// <summary>The shell's icon for an item (a packaged app's <c>shell:AppsFolder\…</c>, a shortcut). UI (STA) thread.</summary>
    public ImageSource? ForShellItem(string parsingName)
    {
        if (!_byShellItem.TryGetValue(parsingName, out var bitmap))
        {
            bitmap = ToBitmap(provider.GetShellItemIcon(parsingName, PinIconPixelSize));
            _byShellItem[parsingName] = bitmap;
        }

        return bitmap;
    }

    /// <summary>A sync rendered new pin icons: forget the pins' bitmaps so the next lookup takes them.</summary>
    public void InvalidatePins() => _byPin.Clear();

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
        _byPin.Clear();
        _byShellItem.Clear();
        _byWindow.Clear();
        _byPixels.Clear();
    }
}
