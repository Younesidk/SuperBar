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
    public DataTemplate? ClockTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        StartSectionViewModel => StartTemplate,
        AppsSectionViewModel => AppsTemplate,
        WidgetSectionViewModel => WidgetTemplate,
        SystemTraySectionViewModel => SystemTrayTemplate,
        ClockSectionViewModel => ClockTemplate,
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
/// Hosts one widget's UI element in its taskbar slot. A UI element can only have one parent, and the zone
/// item controls re-create their containers when sections move, so the slot detaches the element from any
/// previous host before adopting it, and releases it when unloaded.
/// </summary>
public sealed partial class WidgetSlot : ContentControl
{
    public static readonly DependencyProperty ElementProperty = DependencyProperty.Register(
        nameof(Element), typeof(UIElement), typeof(WidgetSlot), new PropertyMetadata(null, (d, _) => ((WidgetSlot)d).Attach()));

    public WidgetSlot()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsTabStop = false;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(Content, Element))
            {
                Content = null;
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

        if (element is not null)
        {
            Detach(element);
        }

        Content = element;
    }

    private void Detach(UIElement element)
    {
        if (element is FrameworkElement { Parent: WidgetSlot other } && !ReferenceEquals(other, this))
        {
            other.Content = null;
        }

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
