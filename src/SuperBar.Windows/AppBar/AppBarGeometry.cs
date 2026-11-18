using SuperBar.Core.Geometry;
using SuperBar.Core.Shell;

namespace SuperBar.Windows.AppBar;

/// <summary>Pure rectangle math for the ABM_QUERYPOS / ABM_SETPOS negotiation.</summary>
internal static class AppBarGeometry
{
    public static int ToPhysicalThickness(double thicknessDip, double scale) =>
        Math.Max(1, (int)Math.Round(thicknessDip * (scale <= 0 ? 1 : scale), MidpointRounding.AwayFromZero));

    /// <summary>The rectangle proposed to the shell: the full monitor width along the requested edge.</summary>
    public static ScreenRect Propose(ScreenRect monitor, ScreenEdge edge, int thickness) => edge switch
    {
        ScreenEdge.Top => new ScreenRect(monitor.Left, monitor.Top, monitor.Right, monitor.Top + thickness),
        _ => new ScreenRect(monitor.Left, monitor.Bottom - thickness, monitor.Right, monitor.Bottom),
    };

    /// <summary>
    /// ABM_QUERYPOS may move the edge we are docked to (e.g. another AppBar already owns the bottom strip);
    /// it does not preserve our thickness, so re-apply it from the adjusted docking edge.
    /// </summary>
    public static ScreenRect EnforceThickness(ScreenRect adjusted, ScreenEdge edge, int thickness) => edge switch
    {
        ScreenEdge.Top => new ScreenRect(adjusted.Left, adjusted.Top, adjusted.Right, adjusted.Top + thickness),
        _ => new ScreenRect(adjusted.Left, adjusted.Bottom - thickness, adjusted.Right, adjusted.Bottom),
    };
}
