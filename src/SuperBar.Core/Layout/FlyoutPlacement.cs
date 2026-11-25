using SuperBar.Core.Shell;

namespace SuperBar.Core.Layout;

/// <summary>
/// Where a widget flyout opens. All values are DIPs in the bar's own coordinates: the bar spans its whole monitor,
/// so clamping to the bar is clamping to the monitor, and the UI framework converts DIPs to the monitor's DPI.
/// </summary>
public static class FlyoutPlacement
{
    /// <summary>Space kept between a flyout and the monitor's left and right edges.</summary>
    public const double EdgeMargin = 8;

    /// <summary>
    /// The horizontal center of a <paramref name="flyoutWidth"/>-wide flyout: on its anchor when possible, shifted so it
    /// stays <paramref name="margin"/> inside both ends of the bar. A flyout wider than the bar is centered on it.
    /// </summary>
    public static double CenterX(double anchorCenter, double flyoutWidth, double barWidth, double margin = EdgeMargin)
    {
        var half = Math.Max(0, flyoutWidth) / 2;
        var min = margin + half;
        var max = barWidth - margin - half;
        return max < min ? barWidth / 2 : Math.Clamp(anchorCenter, min, max);
    }

    /// <summary>A flyout opens away from the screen edge the bar sits on: above a bottom bar, below a top bar.</summary>
    public static bool OpensUpward(ScreenEdge barEdge) => barEdge != ScreenEdge.Top;
}
