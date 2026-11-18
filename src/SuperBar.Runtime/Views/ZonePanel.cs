using Avalonia;
using Avalonia.Controls;
using SuperBar.Core.Layout;
using SuperBar.Core.Settings;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Lays out the three taskbar zones on one continuous surface (see <see cref="ZoneLayoutCalculator"/>):
/// left zone at the start, right zone at the end, center zone truly centered when it fits.
/// </summary>
public sealed class ZonePanel : Panel
{
    public static readonly AttachedProperty<TaskbarZone> ZoneProperty =
        AvaloniaProperty.RegisterAttached<ZonePanel, Control, TaskbarZone>("Zone");

    static ZonePanel()
    {
        AffectsParentMeasure<ZonePanel>(ZoneProperty);
    }

    public static TaskbarZone GetZone(Control control) => control.GetValue(ZoneProperty);

    public static void SetZone(Control control, TaskbarZone value) => control.SetValue(ZoneProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control? left = null, center = null, right = null;
        foreach (var child in Children)
        {
            switch (GetZone(child))
            {
                case TaskbarZone.Left: left ??= child; break;
                case TaskbarZone.Center: center ??= child; break;
                case TaskbarZone.Right: right ??= child; break;
            }
        }

        var a = ZoneLayoutCalculator.Compute(
            finalSize.Width,
            left?.DesiredSize.Width ?? 0,
            center?.DesiredSize.Width ?? 0,
            right?.DesiredSize.Width ?? 0);

        left?.Arrange(new Rect(a.LeftX, 0, a.LeftWidth, finalSize.Height));
        center?.Arrange(new Rect(a.CenterX, 0, a.CenterWidth, finalSize.Height));
        right?.Arrange(new Rect(a.RightX, 0, a.RightWidth, finalSize.Height));
        return finalSize;
    }
}
