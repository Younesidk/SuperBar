using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SuperBar.ControlCenter;

/// <summary>
/// The app icon files that ship beside SuperBar.exe (in <c>Assets</c>). Paths are absolute, built from the app's
/// base directory, so they resolve whatever the working directory was when SuperBar started.
/// </summary>
internal static class AppAssets
{
    /// <summary>The multi-size .ico (16 to 256 px), for window icons.</summary>
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "SuperBar.ico");

    /// <summary>The 256 px .png, for showing the icon inside the UI.</summary>
    public static string ImagePath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "SuperBar.png");

    /// <summary>
    /// The app icon as an image that is shown <paramref name="sizeDip"/> wide. Decoded at that size times the display
    /// scale, so it stays sharp instead of being shrunk from 256 px while drawing. Null when the file is missing.
    /// UI thread only.
    /// </summary>
    public static ImageSource? LoadImage(int sizeDip)
    {
        if (!File.Exists(ImagePath))
        {
            return null;
        }

        // The decode size is set before the source: it only applies to a decode that has not started yet.
        var image = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = sizeDip,
            DecodePixelHeight = sizeDip,
        };
        image.UriSource = new Uri(ImagePath);
        return image;
    }
}
