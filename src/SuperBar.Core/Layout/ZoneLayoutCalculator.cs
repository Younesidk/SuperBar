namespace SuperBar.Core.Layout;

public readonly record struct ZoneArrangement(double LeftX, double LeftWidth, double CenterX, double CenterWidth, double RightX, double RightWidth);

/// <summary>
/// Positions the three taskbar zones on one continuous surface:
/// the left zone hugs the start edge, the right zone hugs the end edge and the center zone is
/// centered on the whole bar when possible, otherwise pushed so it never overlaps its neighbours.
/// </summary>
public static class ZoneLayoutCalculator
{
    public static ZoneArrangement Compute(double totalWidth, double leftWidth, double centerWidth, double rightWidth)
    {
        totalWidth = Math.Max(0, totalWidth);
        leftWidth = Math.Max(0, leftWidth);
        centerWidth = Math.Max(0, centerWidth);
        rightWidth = Math.Max(0, rightWidth);

        // The system section (right) has priority: it always stays anchored to the end edge.
        var rightX = Math.Max(0, totalWidth - rightWidth);

        // The left zone may shrink when space runs out (overflow handling is a future milestone).
        var effectiveLeft = Math.Min(leftWidth, rightX);

        var minCenter = effectiveLeft;
        var maxCenter = rightX - centerWidth;
        var ideal = (totalWidth - centerWidth) / 2;

        double centerX;
        double effectiveCenter = centerWidth;
        if (maxCenter < minCenter)
        {
            centerX = minCenter;
            effectiveCenter = Math.Max(0, rightX - minCenter);
        }
        else
        {
            centerX = Math.Clamp(ideal, minCenter, maxCenter);
        }

        return new ZoneArrangement(0, effectiveLeft, centerX, effectiveCenter, rightX, Math.Min(rightWidth, totalWidth));
    }
}
