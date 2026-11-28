using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SuperBar.Runtime.ViewModels;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Picks the template of each taskbar section (WinUI has no implicit, type-keyed DataTemplates).
/// </summary>
public sealed partial class SectionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StartTemplate { get; set; }
    public DataTemplate? AppsTemplate { get; set; }
    public DataTemplate? WidgetTemplate { get; set; }
    public DataTemplate? SystemTrayTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        StartSectionViewModel => StartTemplate,
        AppsSectionViewModel => AppsTemplate,
        WidgetSectionViewModel => WidgetTemplate,
        SystemTraySectionViewModel => SystemTrayTemplate,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Brush lookups for x:Bind function bindings (an x:Bind <c>Converter</c> cannot be used in <c>Window</c> XAML).
/// Returns the shared theme brushes, so colors stay live.
/// </summary>
public static class TaskbarBrushes
{
    /// <summary>Application button background: highlighted while its window is the active one.</summary>
    public static Brush? ItemBackground(bool isActive) =>
        Application.Current.Resources[isActive ? "SB.ActiveItem" : "SB.Transparent"] as Brush;

    /// <summary>The stacked "more windows" layer shows while a grouped app is hovered or active.</summary>
    public static double StackOpacity(bool hasMultipleWindows, bool isHovered, bool isActive) =>
        hasMultipleWindows && (isHovered || isActive) ? 1 : 0;
}

/// <summary>
/// The Windows 11 style "more windows" layer of a grouped application button: a second plate behind the button,
/// shifted to the right. Only the strip that sticks out past the button is drawn, because the button's own
/// translucent hover background would otherwise double up over the plate.
/// </summary>
public sealed partial class StackPlate : Grid
{
    public const double Offset = 4;
    public const double Inset = 4;

    public StackPlate()
    {
        IsHitTestVisible = false;
        Margin = new Thickness(Offset, Inset, -Offset, Inset);
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        SizeChanged += (_, e) => Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(Math.Max(0, e.NewSize.Width - Offset), 0, Offset, e.NewSize.Height),
        };
    }
}

/// <summary>
/// The running-app pill under an application button: a subtle 6 px dot that grows to a 16 px accent bar
/// (150 ms) when the app's window is active.
/// </summary>
public sealed partial class RunningIndicator : Grid
{
    public const double InactiveWidth = 6;
    public const double ActiveWidth = 16;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(RunningIndicator), new PropertyMetadata(false, (d, _) => ((RunningIndicator)d).Update(animate: true)));

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(RunningIndicator), new PropertyMetadata(null, (d, _) => ((RunningIndicator)d).Update(animate: false)));

    public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
        nameof(InactiveBrush), typeof(Brush), typeof(RunningIndicator), new PropertyMetadata(null, (d, _) => ((RunningIndicator)d).Update(animate: false)));

    private Storyboard? _storyboard;

    public RunningIndicator()
    {
        Width = InactiveWidth;
        Height = 3;
        CornerRadius = new CornerRadius(2);
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(0, 0, 0, 2);
        IsHitTestVisible = false;
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public Brush? ActiveBrush { get => (Brush?)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }

    public Brush? InactiveBrush { get => (Brush?)GetValue(InactiveBrushProperty); set => SetValue(InactiveBrushProperty, value); }

    private void Update(bool animate)
    {
        Background = IsActive ? ActiveBrush : InactiveBrush;
        var target = IsActive ? ActiveWidth : InactiveWidth;

        _storyboard?.Stop();
        _storyboard = null;
        if (!animate || !IsLoaded)
        {
            Width = target;
            return;
        }

        var from = Width;
        var animation = new DoubleAnimation
        {
            From = double.IsNaN(from) ? InactiveWidth : from,
            To = target,
            Duration = TimeSpan.FromMilliseconds(150),
            EnableDependentAnimation = true, // Width is a layout property
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Width));
        var storyboard = new Storyboard { Children = { animation } };
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_storyboard, storyboard))
            {
                _storyboard = null;
            }
        };
        _storyboard = storyboard;
        storyboard.Begin();
    }
}

/// <summary>
/// The red "needs attention" plate behind an application button, shown when one of its windows flashes
/// (FlashWindowEx: a new message, an incoming call). Like the Windows taskbar it pulses a few times (smooth fades),
/// then stays lit until the window is activated, when it fades out.
/// </summary>
public sealed partial class AttentionHighlight : Grid
{
    public const int Blinks = 4;
    public const double DimOpacity = 0.2;
    public static readonly TimeSpan BlinkDuration = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(250);

    public static readonly DependencyProperty IsFlashingProperty = DependencyProperty.Register(
        nameof(IsFlashing), typeof(bool), typeof(AttentionHighlight), new PropertyMetadata(false, (d, _) => ((AttentionHighlight)d).Update()));

    private Storyboard? _storyboard;

    public AttentionHighlight()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += (_, _) => Update();
    }

    public bool IsFlashing { get => (bool)GetValue(IsFlashingProperty); set => SetValue(IsFlashingProperty, value); }

    private void Update()
    {
        // Continue from wherever a running animation left the plate, so interruptions never jump.
        var current = Opacity;
        _storyboard?.Stop();
        _storyboard = null;

        if (!IsLoaded)
        {
            Opacity = IsFlashing ? 1 : 0;
            return;
        }

        Opacity = current;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        if (IsFlashing)
        {
            // Breathe: fade in, fade most of the way out, a few times; then fade in and stay lit.
            var half = BlinkDuration / 2;
            for (var i = 0; i < Blinks; i++)
            {
                var start = BlinkDuration * i;
                animation.KeyFrames.Add(Eased(start + half, 1));
                animation.KeyFrames.Add(Eased(start + BlinkDuration, DimOpacity));
            }

            animation.KeyFrames.Add(Eased(BlinkDuration * Blinks + half, 1));
        }
        else
        {
            animation.KeyFrames.Add(Eased(FadeOutDuration, 0));
        }

        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Opacity));
        _storyboard = new Storyboard { Children = { animation } };
        _storyboard.Begin();
    }

    private static EasingDoubleKeyFrame Eased(TimeSpan at, double value) => new()
    {
        KeyTime = KeyTime.FromTimeSpan(at),
        Value = value,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };
}

/// <summary>
/// The root of an application button: plays its appear animation when an app opens and its disappear animation when
/// it closes, instead of the button popping in or out.
/// <para>
/// <see cref="Reveal"/> (0 = gone, 1 = fully there) drives both: the slot the button takes in the row grows or shrinks
/// with it, so the neighbouring buttons slide over smoothly, while the content fades and scales from its centre. The
/// row's spacing is folded in too (a negative right margin), so nothing jumps when the button is finally removed.
/// </para>
/// </summary>
public sealed partial class AppPresence : Grid
{
    public static readonly TimeSpan AppearDuration = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan DisappearDuration = TimeSpan.FromMilliseconds(200);
    private const double StartScale = 0.5;

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(AppPresence), new PropertyMetadata(1.0, (d, _) => ((AppPresence)d).ApplyReveal()));

    public static readonly DependencyProperty IsLeavingProperty = DependencyProperty.Register(
        nameof(IsLeaving), typeof(bool), typeof(AppPresence), new PropertyMetadata(false, (d, _) => ((AppPresence)d).OnIsLeavingChanged()));

    private readonly ScaleTransform _scale = new();
    private Storyboard? _storyboard;
    private double _naturalWidth;

    public AppPresence()
    {
        RenderTransform = _scale;
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>How much of the button is there, from 0 (gone) to 1. Animated.</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    /// <summary>The app closed: animate out, then take the button off the bar.</summary>
    public bool IsLeaving { get => (bool)GetValue(IsLeavingProperty); set => SetValue(IsLeavingProperty, value); }

    private AppButtonViewModel? App => DataContext as AppButtonViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App is { IsLeaving: true })
        {
            Animate(to: 0); // re-created mid-animation (the row was reordered)
        }
        else if (App is { AnimateIn: true } app)
        {
            app.AnimateIn = false;
            Reveal = 0;
            Animate(to: 1);
        }
        else if (App is { } settled)
        {
            settled.IsAnimating = false; // an earlier element for this button was unloaded mid-animation
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _storyboard?.Stop();
        _storyboard = null;
        if (App is { } app)
        {
            app.IsAnimating = false;
        }
    }

    private void OnIsLeavingChanged()
    {
        IsHitTestVisible = !IsLeaving;
        if (!IsLoaded)
        {
            return;
        }

        Animate(to: IsLeaving ? 0 : 1);
    }

    private void Animate(double to)
    {
        // Continue from where a running animation is, so reopening an app mid-close turns it around smoothly.
        var from = Reveal;
        _storyboard?.Stop();
        Reveal = from;

        if (App is { } app)
        {
            app.IsAnimating = true;
        }

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = to > from ? AppearDuration : DisappearDuration,
            EnableDependentAnimation = true, // drives layout (the slot's width)
            EasingFunction = new CubicEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Reveal));
        var storyboard = new Storyboard { Children = { animation } };
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_storyboard, storyboard))
            {
                return;
            }

            _storyboard = null;
            if (App is { } current)
            {
                current.IsAnimating = false;
                if (to == 0 && current.IsLeaving)
                {
                    current.CompleteRemoval();
                }
            }
        };
        _storyboard = storyboard;
        storyboard.Begin();
    }

    private void ApplyReveal()
    {
        var reveal = Math.Clamp(Reveal, 0, 1);
        Opacity = reveal;
        _scale.ScaleX = _scale.ScaleY = StartScale + (1 - StartScale) * reveal;
        Margin = new Thickness(0, 0, -(1 - reveal) * ItemSpacing(), 0);
        InvalidateMeasure();
    }

    /// <summary>The spacing the row's panel puts after this button (the item container's parent).</summary>
    private double ItemSpacing()
    {
        DependencyObject? node = this;
        for (var i = 0; i < 3 && node is not null; i++)
        {
            node = VisualTreeHelper.GetParent(node);
            if (node is StackPanel panel)
            {
                return panel.Spacing;
            }
        }

        return 0;
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var natural = base.MeasureOverride(availableSize);
        _naturalWidth = natural.Width;
        return new Windows.Foundation.Size(natural.Width * Math.Clamp(Reveal, 0, 1), natural.Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        // Lay the content out at full size, centred in the (narrower) slot: it is scaled down, not squeezed.
        var width = Math.Max(_naturalWidth, finalSize.Width);
        var rect = new Windows.Foundation.Rect((finalSize.Width - width) / 2, 0, width, finalSize.Height);
        foreach (var child in Children)
        {
            child.Arrange(rect);
        }

        return finalSize;
    }
}

/// <summary>
/// Collapses a zone item completely: the template root and the items control's container around it, so the zone's
/// panel adds no spacing for it either. Set on the root of an item template.
/// </summary>
public static class ZoneItem
{
    public static readonly DependencyProperty CollapsedProperty = DependencyProperty.RegisterAttached(
        "Collapsed", typeof(bool), typeof(ZoneItem), new PropertyMetadata(false, OnCollapsedChanged));

    public static bool GetCollapsed(DependencyObject element) => (bool)element.GetValue(CollapsedProperty);

    public static void SetCollapsed(DependencyObject element, bool value) => element.SetValue(CollapsedProperty, value);

    private static void OnCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        // The container may not exist yet (or be replaced when the item moves): apply again once loaded.
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        Apply(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var visibility = GetCollapsed(element) ? Visibility.Collapsed : Visibility.Visible;
        element.Visibility = visibility;
        if (VisualTreeHelper.GetParent(element) is ContentPresenter container)
        {
            container.Visibility = visibility;
        }
    }
}

/// <summary>
/// Hosts one widget's UI element in its taskbar slot. A UI element can only have one parent, and the zone
/// item controls re-create their containers when sections move, so a new slot can receive an element that the
/// old (unloading) slot still holds. Every slot therefore records which elements it hosts, and a slot adopting
/// an element first makes its current host let go of it. WinUI does not report a ContentControl as its content's
/// <c>Parent</c>, so this bookkeeping is the only reliable way to find the previous host.
/// </summary>
public sealed partial class WidgetSlot : ContentControl
{
    public static readonly DependencyProperty ElementProperty = DependencyProperty.Register(
        nameof(Element), typeof(UIElement), typeof(WidgetSlot), new PropertyMetadata(null, (d, _) => ((WidgetSlot)d).Attach()));

    // UI thread only. Weak keys: an element that is no longer used anywhere is not kept alive by this table.
    private static readonly ConditionalWeakTable<UIElement, WidgetSlot> Hosts = new();

    public WidgetSlot()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsTabStop = false;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) =>
        {
            // A slot that is loaded again (stale Unloaded after a re-parent) keeps its element.
            if (!IsLoaded && Content is UIElement element)
            {
                Release(element);
            }
        };
    }

    public UIElement? Element { get => (UIElement?)GetValue(ElementProperty); set => SetValue(ElementProperty, value); }

    private void Attach()
    {
        var element = Element;
        if (ReferenceEquals(Content, element))
        {
            return;
        }

        if (Content is UIElement previous)
        {
            Release(previous);
        }

        if (element is not null)
        {
            Detach(element);
        }

        Content = element;
        if (element is not null)
        {
            Hosts.AddOrUpdate(element, this);
        }
    }

    /// <summary>Lets go of <paramref name="element"/> if this slot hosts it.</summary>
    private void Release(UIElement element)
    {
        if (ReferenceEquals(Content, element))
        {
            Content = null;
        }

        if (Hosts.TryGetValue(element, out var host) && ReferenceEquals(host, this))
        {
            Hosts.Remove(element);
        }
    }

    private void Detach(UIElement element)
    {
        if (Hosts.TryGetValue(element, out var host) && !ReferenceEquals(host, this))
        {
            host.Release(element);
        }

        // Fallback for an element that was placed somewhere other than a slot.
        switch (VisualTreeHelper.GetParent(element))
        {
            case ContentPresenter presenter when !ReferenceEquals(presenter.Content, null) && ReferenceEquals(presenter.Content, element):
                presenter.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Border border when ReferenceEquals(border.Child, element):
                border.Child = null;
                break;
        }
    }
}
