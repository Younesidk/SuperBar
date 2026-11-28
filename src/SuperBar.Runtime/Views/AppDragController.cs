using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Layout;
using SuperBar.Runtime.ViewModels;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Drag-to-reorder for the application buttons on the bar.
/// <para>
/// Press an app and move it sideways: once past a small threshold the button lifts and follows the pointer, and the
/// buttons it passes slide aside to open a gap. Releasing settles it into the gap and the new order is committed to
/// the view model (which persists the pinned order). Everything moves through each item container's
/// <see cref="UIElement.Translation"/>, so layout never changes until the drop: the motion is a GPU animation, and a
/// cancelled drag (capture lost, e.g. Alt+Tab) simply glides back.
/// </para>
/// </summary>
internal sealed class AppDragController
{
    private const double StartThreshold = 5;         // DIP of sideways movement before a press becomes a drag
    private const double LiftScale = 1.1;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan ClickSuppression = TimeSpan.FromMilliseconds(300);

    private readonly ConditionalWeakTable<UIElement, object> _attached = new();
    private readonly Action _onDragStarted;

    private AppButtonViewModel? _pressedApp;
    private FrameworkElement? _pressedItem;
    private Panel? _panel;
    private uint _pointerId;
    private double _pressX;
    private bool _dragging;
    private bool _swappingCapture;
    private int _from;
    private int _target;
    private UIElement[] _containers = [];
    private double[] _offsets = [];
    private double _itemWidth;
    private AppsSectionViewModel? _section;
    private DateTime _suppressClicksUntil;
    private DispatcherQueueTimer? _settleTimer;

    /// <param name="onDragStarted">Called when a press turns into a drag (the bar hides the window preview).</param>
    public AppDragController(Action onDragStarted) => _onDragStarted = onDragStarted;

    public bool IsDragging => _dragging;

    /// <summary>True for the click that ends a drag: it must not also activate or minimize the app.</summary>
    public bool ShouldSuppressClick => _dragging || DateTime.UtcNow < _suppressClicksUntil;

    /// <summary>Wires an app item's root element (the button's template root). Safe to call on every Loaded.</summary>
    public void Attach(FrameworkElement item)
    {
        if (_attached.TryGetValue(item, out _))
        {
            return;
        }

        _attached.Add(item, new object());

        // The button marks pointer events handled, so listen to handled events too.
        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        item.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        item.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), handledEventsToo: true);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging || _settleTimer?.IsRunning == true || sender is not FrameworkElement { DataContext: AppButtonViewModel app } item)
        {
            return;
        }

        var point = e.GetCurrentPoint(null);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedApp = app;
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

        var x = e.GetCurrentPoint(null).Position.X;
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                Reset();
                return;
            }

            if (Math.Abs(x - _pressX) < StartThreshold || !TryStart(e))
            {
                return;
            }
        }

        Follow(x - _pressX);
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

        // Something else took the mouse (Alt+Tab, a system dialog): put the button back.
        Drop(commit: false);
    }

    private bool TryStart(PointerRoutedEventArgs e)
    {
        var item = _pressedItem!;
        var container = ContainerOf(item, out var panel);
        if (container is null || panel is null || panel.Children.Count < 2 || _pressedApp is null)
        {
            Reset();
            return false;
        }

        _panel = panel;
        _containers = panel.Children.ToArray();
        _from = Array.IndexOf(_containers, container);
        if (_from < 0)
        {
            Reset();
            return false;
        }

        _offsets = _containers.Select(c => c.TransformToVisual(panel).TransformPoint(default).X).ToArray();
        _itemWidth = container.ActualSize.X;
        _target = _from;
        _section = FindSection(panel);
        if (_section?.IsAnimating == true)
        {
            Reset(); // a button is appearing or disappearing: the slots are still moving
            return false;
        }

        _section?.BeginReorder();
        _dragging = true;

        // Take the pointer from the button so it leaves its pressed state and does not click on release.
        _swappingCapture = true;
        foreach (var button in Descendants<Button>(item))
        {
            button.ReleasePointerCaptures();
        }

        item.CapturePointer(e.Pointer);
        _swappingCapture = false;

        foreach (var c in _containers)
        {
            c.TranslationTransition = new Vector3Transition { Duration = SlideDuration, Components = Vector3TransitionComponents.X };
        }

        container.TranslationTransition = null; // follows the pointer 1:1
        container.ScaleTransition = new Vector3Transition { Duration = SettleDuration };
        container.CenterPoint = new Vector3((float)(_itemWidth / 2), container.ActualSize.Y / 2, 0);
        container.Scale = new Vector3((float)LiftScale, (float)LiftScale, 1);
        Canvas.SetZIndex(container, 100);

        _pressedApp.IsHovered = false;
        _onDragStarted();
        return true;
    }

    private void Follow(double dx)
    {
        var dragged = _containers[_from];

        // Keep the button inside the row.
        dx = Math.Clamp(dx, _offsets[0] - _offsets[_from], _offsets[^1] - _offsets[_from]);
        dragged.Translation = new Vector3((float)dx, 0, 0);

        var centers = _offsets.Select(o => o + _itemWidth / 2).ToArray();
        var target = AppOrdering.TargetIndex(centers, _offsets[_from] + dx + _itemWidth / 2);
        if (target == _target)
        {
            return;
        }

        _target = target;
        for (var i = 0; i < _containers.Length; i++)
        {
            if (i == _from)
            {
                continue;
            }

            var shift = AppOrdering.ShiftDirection(i, _from, _target) switch
            {
                < 0 => _offsets[i - 1] - _offsets[i],
                > 0 => _offsets[i + 1] - _offsets[i],
                _ => 0,
            };
            _containers[i].Translation = new Vector3((float)shift, 0, 0);
        }
    }

    private void Drop(bool commit)
    {
        var target = commit ? _target : _from;
        var dragged = _containers[_from];

        _dragging = false;
        _suppressClicksUntil = DateTime.UtcNow + ClickSuppression;
        _pressedItem?.ReleasePointerCaptures();

        // Settle: the dragged button glides into its slot and lands; the others finish (or undo) their slide.
        dragged.TranslationTransition = new Vector3Transition { Duration = SettleDuration, Components = Vector3TransitionComponents.X };
        dragged.Translation = new Vector3((float)(_offsets[target] - _offsets[_from]), 0, 0);
        dragged.Scale = Vector3.One;
        if (!commit)
        {
            foreach (var c in _containers)
            {
                c.Translation = Vector3.Zero;
            }
        }

        var containers = _containers;
        var from = _from;
        var section = _section;
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

            // Commit the order, then drop every offset in the same frame: each button is already drawn where its
            // new layout slot is, so nothing visibly moves.
            section?.EndReorder(from, target);
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
        _pressedApp = null;
        _pressedItem = null;
        _panel = null;
        _section = null;
        _containers = [];
        _offsets = [];
    }

    /// <summary>The item container (direct child of the items panel) holding <paramref name="element"/>.</summary>
    private static UIElement? ContainerOf(DependencyObject element, out Panel? panel)
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

    private static AppsSectionViewModel? FindSection(DependencyObject element)
    {
        var current = element;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is FrameworkElement { DataContext: AppsSectionViewModel section })
            {
                return section;
            }

            current = parent;
        }

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
}
