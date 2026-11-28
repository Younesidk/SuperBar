namespace SuperBar.Core.Geometry;

/// <summary>DIP to physical-pixel conversion, shared so every caller rounds a size the same way.</summary>
public static class DpiScale
{
    /// <summary>
    /// <paramref name="dip"/> device-independent pixels at <paramref name="scale"/> (DPI / 96), rounded half away
    /// from zero, never below 1 px. A non-positive scale counts as 100%.
    /// </summary>
    public static int ToPhysical(double dip, double scale) =>
        Math.Max(1, (int)Math.Round(dip * (scale <= 0 ? 1 : scale), MidpointRounding.AwayFromZero));
}
