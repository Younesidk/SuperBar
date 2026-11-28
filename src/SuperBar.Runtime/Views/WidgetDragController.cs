using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using SuperBar.Core.Layout;
using SuperBar.Runtime.ViewModels;
using Windows.Foundation;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Drag-to-reorder for the widgets on the bar (application buttons have their own <see cref="AppDragController"/>).
/// <para>
/// Press a widget and move it sideways: past a small threshold it lifts and follows the pointer along the bar, and the
/// sections it passes slide aside so the row stays tightly packed. Whenever it locks into a new slot, a blue glow lights
/// the slot's two edges; on release the widget glides into the slot and the glow fades. The new order is committed to
/// the view model, which persists it in the layout.
/// </para>
/// <para>
/// A widget moves within its "run": the neighbouring widgets and the system section of its zone. Start and the
/// application buttons never move and cannot be crossed. As with the app buttons, everything moves through each item
/// container's <see cref="UIElement.Translation"/>, so layout only changes at the drop and a cancelled drag (capture
/// lost) simply glides back.
/// </para>
/// </summary>
internal sealed class WidgetDragController
{
    private const double StartThreshold = 6;          // DIP of sideways movement before a press becomes a drag
    private const double LiftScale = 1.04;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(200);

    private readonly ConditionalWeakTable<UIElement, object> _attached = new();
    private readonly TaskbarViewModel _viewModel;
    private readonly SnapGlow _glow;
    private readonly Action _onDragStarted;

    private FrameworkElement? _pressedItem;
    private uint _pointerId;
    private double _pressX;
    private bool _dragging;
    private bool _swappingCapture;
    private Panel? _panel;
    private UIElement[] _containers = [];             // the run, in bar order
    private SectionViewModel[] _sections = [];
    private double[] _offsets = [];
    private double[] _widths = [];
    private double _spacing;
    private int _from;
    private int _target;
    private DispatcherQueueTimer? _settleTimer;

    /// <param name="overlay">A canvas covering the bar's zones, above them: the snap glow is drawn there.</param>
    /// <param name="onDragStarted">Called when a press turns into a drag (the bar closes widget flyouts).</param>
    public WidgetDragController(TaskbarViewModel viewModel, Canvas overlay, Action onDragStarted)
    {
        _viewModel = viewModel;
        _glow = new SnapGlow(overlay);
        _onDragStarted = onDragStarted;
    }

    public bool IsDragging => _dragging;

    /// <summary>Wires a widget's slot (the root of the widget template). Safe to call on every Loaded.</summary>
    public void Attach(FrameworkElement item)
    {
        if (_attached.TryGetValue(item, out _))
        {
            return;
        }

        _attached.Add(item, new object());

        // Buttons inside the widget mark pointer events handled, so listen to handled events too.
        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        item.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), handledEventsToo: true);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _settleTimer?.IsRunning == true || sender is not FrameworkElement { DataContext: WidgetSectionViewModel } item)
        {
            return;
        }

        var point = e.GetCurrentPoint(null);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedItem = item;
        _pointerId = e.Pointer.PointerId;
        _pressX = point.Position.X;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedItem is null || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(null);
        var x = point.Position.X;
        if (!_dragging)
        {
            if (!point.Properties.IsLeftButtonPressed)
            {
                Reset();
                return;
            }

            // Below the threshold this is still a click on whatever control is under the pointer.
            if (Math.Abs(x - _pressX) < StartThreshold || !TryStart(e))
            {
                return;
            }
        }

        Follow(x - _pressX); // horizontal only: the vertical movement is ignored
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        if (_dragging)
        {
            Drop(commit: true);
        }
        else
        {
            Reset();
        }
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_swappingCapture || !_dragging || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        // Something else took the mouse (Alt+Tab, a system dialog): put the widget back.
        Drop(commit: false);
    }

    private bool TryStart(PointerRoutedEventArgs e)
    {
        var item = _pressedItem!;
        var container = ContainerOf(item, out var panel);
        if (container is null || panel is null || !CollectRun(panel, container))
        {
            Reset();
            return false;
        }

        _panel = panel;
        _spacing = panel.Spacing;
        _offsets = _containers.Select(c => c.TransformToVisual(panel).TransformPoint(default).X).ToArray();
        _widths = _containers.Select(c => (double)c.ActualSize.X).ToArray();
        _target = _from;
        _dragging = true;

        // Take the pointer from the control that was pressed (play/pause, the clock button, …) so it leaves its
        // pressed state and does not click on release.
        _swappingCapture = true;
        foreach (var button in Descendants<ButtonBase>(item))
        {
            button.ReleasePointerCaptures();
        }

        item.CapturePointer(e.Pointer);
        _swappingCapture = false;

        foreach (var c in _containers)
        {
            c.TranslationTransition = new Vector3Transition { Duration = SlideDuration, Components = Vector3TransitionComponents.X };
        }

        // The dragged widget follows the pointer 1:1, slightly enlarged and above its neighbours.
        container.TranslationTransition = null;
        container.ScaleTransition = new Vector3Transition { Duration = SettleDuration };
        container.CenterPoint = new Vector3(container.ActualSize.X / 2, container.ActualSize.Y / 2, 0);
        container.Scale = new Vector3((float)LiftScale, (float)LiftScale, 1);
        Canvas.SetZIndex(container, 100);

        _onDragStarted();
        return true;
    }

    /// <summary>
    /// Finds the run around <paramref name="container"/>: the visible widgets and system section next to it, up to the
    /// zone's ends or a section that never moves (Start, the applications). False when there is nothing to reorder.
    /// </summary>
    private bool CollectRun(StackPanel panel, UIElement container)
    {
        var children = panel.Children.ToList();
        var index = children.IndexOf(container);
        if (index < 0)
        {
            return false;
        }

        var run = new List<UIElement> { container };
        for (var i = index - 1; i >= 0 && !IsFixed(children[i]); i--)
        {
            if (IsShown(children[i]))
            {
                run.Insert(0, children[i]);
            }
        }

        _from = run.Count - 1;
        for (var i = index + 1; i < children.Count && !IsFixed(children[i]); i++)
        {
            if (IsShown(children[i]))
            {
                run.Add(children[i]);
            }
        }

        _containers = run.ToArray();
        _sections = run.Select(SectionOf).OfType<SectionViewModel>().ToArray();
        return run.Count >= 2 && _sections.Length == run.Count;
    }

    private void Follow(double dx)
    {
        // Keep the widget inside its run.
        var width = _widths[_from];
        dx = Math.Clamp(dx, _offsets[0] - _offsets[_from], _offsets[^1] + _widths[^1] - width - _offsets[_from]);
        _containers[_from].Translation = new Vector3((float)dx, 0, 0);

        var target = SectionOrdering.TargetIndex(_widths, _offsets[0], _spacing, _from, _offsets[_from] + dx + width / 2);
        if (target == _target)
        {
            return;
        }

        // Locked into another slot: the others close ranks around it, and the slot's edges light up.
        _target = target;
        var positions = SectionOrdering.Positions(_widths, _offsets[0], _spacing, _from, _target);
        for (var i = 0; i < _containers.Length; i++)
        {
            if (i != _from)
            {
                _containers[i].Translation = new Vector3((float)(positions[i] - _offsets[i]), 0, 0);
            }
        }

        if (_panel is { } panel)
        {
            var top = _containers[_from].TransformToVisual(panel).TransformPoint(default).Y;
            _glow.Show(panel, positions[_from] - _spacing / 2, positions[_from] + width + _spacing / 2, top, _containers[_from].ActualSize.Y);
        }
    }

    private void Drop(bool commit)
    {
        var target = commit ? _target : _from;
        var dragged = _containers[_from];

        _dragging = false;
        _pressedItem?.ReleasePointerCaptures();

        // Settle: the dragged widget glides into its slot and lands; the others finish (or undo) their slide.
        var positions = SectionOrdering.Positions(_widths, _offsets[0], _spacing, _from, target);
        dragged.TranslationTransition = new Vector3Transition { Duration = SettleDuration, Components = Vector3TransitionComponents.X };
        for (var i = 0; i < _containers.Length; i++)
        {
            _containers[i].Translation = new Vector3((float)(positions[i] - _offsets[i]), 0, 0);
        }

        dragged.Scale = Vector3.One;
        _glow.Hide(gently: commit);

        var containers = _containers;
        var sections = _sections;
        var from = _from;
        var panel = _panel;

        _settleTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settleTimer.IsRepeating = false;
        _settleTimer.Interval = SettleDuration + TimeSpan.FromMilliseconds(20);
        _settleTimer.Tick += Settled;
        _settleTimer.Start();
        Reset();

        void Settled(DispatcherQueueTimer timer, object _)
        {
            timer.Tick -= Settled;

            // Commit the order, then drop every offset in the same frame: each section is already drawn where its
            // new layout slot is, so nothing visibly moves.
            if (target != from)
            {
                var order = sections.ToList();
                order.RemoveAt(from);
                order.Insert(target, sections[from]);
                var after = target > 0;
                _viewModel.MoveSection(sections[from], order[after ? target - 1 : 1], after);
            }

            foreach (var c in containers.Concat(panel?.Children ?? Enumerable.Empty<UIElement>()))
            {
                c.TranslationTransition = null;
                c.Translation = Vector3.Zero;
                c.ScaleTransition = null;
                c.Scale = Vector3.One;
                Canvas.SetZIndex(c, 0);
            }
        }
    }

    private void Reset()
    {
        _pressedItem = null;
        _panel = null;
        _containers = [];
        _sections = [];
        _offsets = [];
        _widths = [];
    }

    private static SectionViewModel? SectionOf(UIElement container) => container switch
    {
        ContentPresenter { Content: SectionViewModel section } => section,
        FrameworkElement { DataContext: SectionViewModel section } => section,
        _ => null,
    };

    /// <summary>Sections that stay where they are and that a dragged widget cannot cross.</summary>
    private static bool IsFixed(UIElement container) =>
        SectionOf(container) is not (WidgetSectionViewModel or SystemTraySectionViewModel);

    /// <summary>A collapsed widget takes no room on the bar: it is skipped, not a barrier.</summary>
    private static bool IsShown(UIElement container) => container.Visibility == Visibility.Visible && container.ActualSize.X > 0;

    /// <summary>The zone's item container (direct child of the zone's panel) holding <paramref name="element"/>.</summary>
    private static UIElement? ContainerOf(DependencyObject element, out StackPanel? panel)
    {
        var current = element;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is StackPanel stack && current is UIElement child)
            {
                panel = stack;
                return child;
            }

            current = parent;
        }

        panel = null;
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// The snap feedback: two soft blue vertical lines, one on each facing edge of the slot the dragged widget has
    /// locked into (next to a neighbour, or along the end of the run). They flash in on every lock and fade out on drop.
    /// </summary>
    private sealed class SnapGlow
    {
        private const double BarWidth = 14;
        private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan CancelDuration = TimeSpan.FromMilliseconds(120);

        private readonly Canvas _overlay;
        private readonly Grid[] _bars = [CreateBar(), CreateBar()];
        private readonly Storyboard?[] _storyboards = new Storyboard?[2];
        private bool _visible;

        public SnapGlow(Canvas overlay) => _overlay = overlay;

        /// <summary>Lights the edges at <paramref name="leftX"/> and <paramref name="rightX"/> (in <paramref name="panel"/> coordinates).</summary>
        public void Show(UIElement panel, double leftX, double rightX, double top, double height)
        {
            var toOverlay = panel.TransformToVisual(_overlay);
            double[] edges = [leftX, rightX];
            for (var i = 0; i < _bars.Length; i++)
            {
                var bar = _bars[i];
                if (bar.Parent is null)
                {
                    _overlay.Children.Add(bar);
                }

                var origin = toOverlay.TransformPoint(new Point(edges[i] - BarWidth / 2, top));
                bar.Height = height;
                Canvas.SetLeft(bar, origin.X);
                Canvas.SetTop(bar, origin.Y);
                Fade(i, from: 0.15, to: 1, FlashDuration);
            }

            _visible = true;
        }

        public void Hide(bool gently)
        {
            if (!_visible)
            {
                return;
            }

            _visible = false;
            for (var i = 0; i < _bars.Length; i++)
            {
                Fade(i, from: null, to: 0, gently ? FadeDuration : CancelDuration);
            }
        }

        private void Fade(int index, double? from, double to, TimeSpan duration)
        {
            // Without a From the animation takes over from wherever the previous one has brought the opacity.
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (from is { } start)
            {
                animation.From = start;
            }

            Storyboard.SetTarget(animation, _bars[index]);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            var storyboard = new Storyboard { Children = { animation } };
            _storyboards[index] = storyboard;
            storyboard.Begin();
        }

        private static Grid CreateBar()
        {
            static Windows.UI.Color Blue(byte alpha) => Windows.UI.Color.FromArgb(alpha, 0x3D, 0x9B, 0xFF);

            var halo = new Rectangle
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = Blue(0x00), Offset = 0 },
                        new GradientStop { Color = Blue(0x66), Offset = 0.5 },
                        new GradientStop { Color = Blue(0x00), Offset = 1 },
                    },
                },
            };
            var line = new Rectangle
            {
                Width = 2,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = new SolidColorBrush(Blue(0xF0)),
            };
            return new Grid { Width = BarWidth, Opacity = 0, IsHitTestVisible = false, Children = { halo, line } };
        }
    }
}
