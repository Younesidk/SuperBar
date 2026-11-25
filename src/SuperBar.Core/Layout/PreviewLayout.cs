using SuperBar.Core.Geometry;

namespace SuperBar.Core.Layout;

/// <summary>One window card of the hover preview, in DIPs.</summary>
/// <param name="ThumbnailWidth">Width of the live thumbnail.</param>
/// <param name="ThumbnailHeight">Height of the live thumbnail.</param>
/// <param name="ContentWidth">Width of the card's content (at least the room needed for the title row).</param>
public readonly record struct PreviewSlot(double ThumbnailWidth, double ThumbnailHeight, double ContentWidth);

/// <summary>Pure sizing and placement math for the window hover preview.</summary>
public static class PreviewLayout
{
    private const double FallbackAspect = 16.0 / 10.0;
    private const double MinShrink = 0.25;

    /// <summary>
    /// Fits a window of <paramref name="source"/> size into <paramref name="maxWidth"/> x <paramref name="maxHeight"/>
    /// keeping its aspect ratio. An unknown size gets a 16:10 box.
    /// </summary>
    public static (double Width, double Height) FitThumbnail(PixelSize source, double maxWidth, double maxHeight)
    {
        if (source.IsEmpty)
        {
            var w = Math.Min(maxWidth, maxHeight * FallbackAspect);
            return (w, w / FallbackAspect);
        }

        var scale = Math.Min(maxWidth / source.Width, maxHeight / source.Height);
        return (source.Width * scale, source.Height * scale);
    }

    /// <summary>
    /// Sizes one card per window. Cards are at least <paramref name="minContentWidth"/> wide; when they do not fit in
    /// <paramref name="availableWidth"/> together (each card also takes <paramref name="cardChrome"/>, plus
    /// <paramref name="spacing"/> between cards) every card shrinks by the same factor.
    /// </summary>
    public static IReadOnlyList<PreviewSlot> Fit(
        IReadOnlyList<PixelSize> sources,
        double maxWidth,
        double maxHeight,
        double minContentWidth,
        double cardChrome,
        double spacing,
        double availableWidth)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var natural = sources.Select(s => FitThumbnail(s, maxWidth, maxHeight)).ToList();
        var fixedWidth = cardChrome * sources.Count + spacing * (sources.Count - 1);
        var contentWidth = natural.Sum(t => Math.Max(t.Width, minContentWidth));

        var factor = 1.0;
        if (fixedWidth + contentWidth > availableWidth)
        {
            factor = Math.Clamp((availableWidth - fixedWidth) / contentWidth, MinShrink, 1);
        }

        return natural
            .Select(t => new PreviewSlot(t.Width * factor, t.Height * factor, Math.Max(t.Width, minContentWidth) * factor))
            .ToList();
    }

    /// <summary>
    /// Places a <paramref name="width"/> x <paramref name="height"/> popup centered above an anchor (a bar button),
    /// <paramref name="gap"/> pixels above <paramref name="barTop"/>, kept <paramref name="margin"/> pixels inside the monitor.
    /// All values are physical pixels.
    /// </summary>
    public static ScreenRect Place(int anchorCenterX, int barTop, int width, int height, int gap, ScreenRect monitor, int margin)
    {
        var x = Math.Min(anchorCenterX - width / 2, monitor.Right - margin - width);
        x = Math.Max(monitor.Left + margin, x);
        var y = Math.Max(monitor.Top + margin, barTop - gap - height);
        return ScreenRect.FromSize(x, y, width, height);
    }
}
