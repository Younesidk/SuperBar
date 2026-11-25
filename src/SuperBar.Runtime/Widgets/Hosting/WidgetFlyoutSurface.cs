using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SuperBar.Core.Layout;
using SuperBar.Core.Shell;
using SuperBar.Widgets;
using Windows.Foundation;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// Shows widget flyouts as native WinUI <see cref="Flyout"/>s opened from the bar. Validated on the real bar (which is
/// activatable, not <c>WS_EX_NOACTIVATE</c>): a click on the bar makes it the foreground window, so a TextBox in the
/// flyout gets real keyboard input, and a click on any other window or the desktop light-dismisses the flyout.
/// <para>
/// The flyout is a windowed popup (<see cref="FlyoutBase.ShouldConstrainToRootBounds"/> off) opened at a point on the
/// bar's edge, so it may extend past the bar and over other windows. The point is the widget slot's center, clamped
/// (in DIPs, see <see cref="FlyoutPlacement"/>) so the flyout stays inside the monitor; WinUI applies the monitor's DPI.
/// </para>
/// UI thread only.
/// </summary>
public sealed class WidgetFlyoutSurface(WidgetViewResolver views, ILogger<WidgetFlyoutSurface> logger) : IWidgetFlyoutSurface
{
    private const double ContentPadding = 16;

    private FrameworkElement? _barRoot;
    private Func<string, FrameworkElement?>? _anchorFor;
    private ScreenEdge _edge = ScreenEdge.Bottom;
    private Flyout? _current;
    private Style? _presenterStyle;

    public event EventHandler? Dismissed;

    /// <summary>The bar exists: flyouts open from <paramref name="barRoot"/>, anchored to the element <paramref name="anchorFor"/> returns for a widget instance.</summary>
    public void Attach(FrameworkElement barRoot, Func<string, FrameworkElement?> anchorFor, ScreenEdge edge)
    {
        _barRoot = barRoot;
        _anchorFor = anchorFor;
        _edge = edge;
    }

    /// <summary>The bar is going away: close what is open.</summary>
    public void Detach()
    {
        Hide();
        _barRoot = null;
        _anchorFor = null;
    }

    public bool Show(string ownerInstanceId, object model)
    {
        if (_barRoot?.XamlRoot is null || _anchorFor?.Invoke(ownerInstanceId) is not { XamlRoot: not null } anchor)
        {
            logger.LogDebug("Flyout of widget {InstanceId} not shown: its slot is not on screen", ownerInstanceId);
            return false;
        }

        Hide(); // one at a time; the coordinator already decided this one replaces the previous

        var view = views.Resolve(model);
        var flyout = new Flyout
        {
            Content = view,
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = _presenterStyle ??= CreatePresenterStyle(),
        };
        flyout.Opened += (_, _) => logger.LogDebug("Flyout of widget {InstanceId} opened", ownerInstanceId);
        flyout.Closed += (_, _) => OnClosed(flyout, view);

        var barRoot = _barRoot;
        var slot = anchor.TransformToVisual(barRoot).TransformBounds(new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
        var anchorCenter = slot.X + slot.Width / 2;
        var upward = FlyoutPlacement.OpensUpward(_edge);
        FlyoutShowOptions At(double width) => new()
        {
            Position = new Point(FlyoutPlacement.CenterX(anchorCenter, width, barRoot.ActualWidth), upward ? 0 : barRoot.ActualHeight),
            Placement = upward ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Bottom,
            ShowMode = FlyoutShowMode.Standard,
        };

        // The clamp needs the flyout's width before it opens, so the view is measured on its own. Views should set
        // an explicit Width: some controls (CalendarView, for one) report the whole available width when measured
        // unconstrained, which would push the flyout toward the middle of the bar.
        view.Measure(new Size(barRoot.ActualWidth, double.PositiveInfinity));
        var width = PresenterWidth(view.DesiredSize.Width);
        if (width >= barRoot.ActualWidth - 2 * FlyoutPlacement.EdgeMargin)
        {
            logger.LogWarning(
                "Flyout view {View} of widget {InstanceId} measured {Width} DIPs wide, the whole bar; give it an explicit Width so it opens at its widget",
                view.GetType().Name, ownerInstanceId, view.DesiredSize.Width);
        }

        _current = flyout;
        flyout.ShowAt(barRoot, At(width));
        return true;
    }

    private static double PresenterWidth(double contentWidth) => contentWidth + 2 * ContentPadding + 2;

    public void Hide()
    {
        var flyout = _current;
        _current = null; // a Closed event for a flyout that is no longer current is not a dismissal
        flyout?.Hide();
    }

    private void OnClosed(Flyout flyout, UIElement view)
    {
        logger.LogDebug("Flyout closed ({Reason})", ReferenceEquals(_current, flyout) ? "dismissed" : "hidden");
        flyout.Content = null;
        if (view is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (ReferenceEquals(_current, flyout))
        {
            // Closed by the user: light dismiss or Escape.
            _current = null;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Style CreatePresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(ContentPadding)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        return style;
    }
}
