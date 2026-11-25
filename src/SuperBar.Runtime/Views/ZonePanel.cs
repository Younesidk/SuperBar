using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Layout;
using SuperBar.Core.Settings;
using Windows.Foundation;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Lays out the three taskbar zones on one continuous surface (see <see cref="ZoneLayoutCalculator"/>):
/// left zone at the start, right zone at the end, center zone truly centered when it fits.
/// </summary>
public sealed class ZonePanel : Panel
{
    public static readonly DependencyProperty ZoneProperty = DependencyProperty.RegisterAttached(
        "Zone",
        typeof(TaskbarZone),
        typeof(ZonePanel),
        new PropertyMetadata(TaskbarZone.Left, OnZoneChanged));

    public static TaskbarZone GetZone(UIElement element) => (TaskbarZone)element.GetValue(ZoneProperty);

    public static void SetZone(UIElement element, TaskbarZone value) => element.SetValue(ZoneProperty, value);

    private static void OnZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && VisualTreeHelper.GetParent(element) is ZonePanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

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
        UIElement? left = null, center = null, right = null;
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
