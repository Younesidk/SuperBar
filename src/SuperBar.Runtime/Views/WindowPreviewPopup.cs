using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Geometry;
using SuperBar.Core.Layout;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace SuperBar.Runtime.Views;

/// <summary>
/// Live window previews above a hovered application button, like the Windows taskbar's: one card per window with
/// the app icon, the window title, a close button and a live thumbnail. Clicking a card switches to that window.
/// <para>
/// The thumbnails are DWM thumbnails: DWM composites them on top of this popup at the rectangles reserved by
/// placeholder elements, so nothing XAML draws can overlap them.
/// </para>
/// Must be used on the UI thread.
/// </summary>
public sealed class WindowPreviewPopup : IDisposable
{
    // Timing (ms), like the Windows taskbar: a short hover before the first preview, then instant switching
    // between buttons, and a grace period to move the pointer from the bar into the popup.
    private const int ShowDelayMs = 400;
    private const int HideDelayMs = 300;
    private const int WatchIntervalMs = 150;

    // Open: fade in while rising into place. Close: fade out while sinking, a little faster.
    // Switching apps while open (also when the pointer races along the bar): the current content fades down, then
    // the new content glides from where the popup was to its new place while fading back up. Hovering further
    // buttons during the fade-down only changes which app comes next, so fast moves never stack animations.
    private const double OpenMs = 180;
    private const double CloseMs = 130;
    private const double SwitchOutMs = 80;
    private const double SwitchInMs = 200;
    private const double SwitchDipOpacity = 0.2;
    private const double SlideDip = 10;

    // Metrics (DIP).
    private const double MaxThumbnailWidth = 200;
    private const double MaxThumbnailHeight = 120;
    private const double MinContentWidth = 140;
    private const double CardPadding = 8;
    private const double HeaderHeight = 24;
    private const double HeaderGap = 6;
    private const double OuterPadding = 6;
    private const double CardSpacing = 4;
    private const double GapAboveBar = 8;
    private const double ScreenMargin = 8;

    private const string CardStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="BorderThickness" Value="0" />
          <Setter Property="HorizontalContentAlignment" Value="Stretch" />
          <Setter Property="VerticalContentAlignment" Value="Stretch" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Grid Background="{TemplateBinding Background}" CornerRadius="6">
                  <VisualStateManager.VisualStateGroups>
                    <VisualStateGroup x:Name="CommonStates">
                      <VisualState x:Name="Normal" />
                      <VisualState x:Name="PointerOver">
                        <VisualState.Setters><Setter Target="Overlay.Background" Value="{StaticResource SB.Hover}" /></VisualState.Setters>
                      </VisualState>
                      <VisualState x:Name="Pressed">
                        <VisualState.Setters><Setter Target="Overlay.Background" Value="{StaticResource SB.Pressed}" /></VisualState.Setters>
                      </VisualState>
                      <VisualState x:Name="Disabled" />
                    </VisualStateGroup>
                  </VisualStateManager.VisualStateGroups>
                  <Border x:Name="Overlay" Background="Transparent" />
                  <ContentPresenter Content="{TemplateBinding Content}" Margin="{TemplateBinding Padding}"
                                    HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch" />
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private readonly IWindowThumbnailService _thumbnails;
    private readonly IWindowChrome _chrome;
    private readonly IWindowSwitcher _switcher;
    private readonly IWindowTracker _tracker;
    private readonly IMonitorService _monitors;
    private readonly IUiDispatcher _dispatcher;
    private readonly TaskbarTheme _theme;
    private readonly ILogger _logger;
    private readonly IUiTimer _showTimer;
    private readonly IUiTimer _hideTimer;
    private readonly IUiTimer _watchTimer;
    private readonly ICursorService _cursor;
    private ScreenRect _bounds;

    /// <summary>
    /// After the popup shrinks under the pointer (a window was closed from it), the old area still counts as "on the
    /// popup" until the pointer leaves it, so closing several windows in a row does not dismiss the preview.
    /// </summary>
    private ScreenRect _grace;
    private readonly List<(FrameworkElement Placeholder, IWindowThumbnail Thumbnail)> _shown = new();

    private Window? _window;
    private Border? _root;
    private StackPanel? _cards;
    private Style? _cardStyle;
    private TaskbarAcrylicBackdrop? _acrylic;
    private AppButtonViewModel? _app;
    private AppButtonViewModel? _shownApp;
    private ScreenRect _anchor;
    private AppButtonViewModel? _pendingApp;
    private ScreenRect _pendingAnchor;
    private string _snapshot = "";
    private bool _visible;
    private bool _refreshQueued;
    private bool _disposed;

    // Animation: the popup window's on-screen state (opacity and physical position) is tweened from _from to _to,
    // one step per rendered frame.
    private readonly record struct Frame(double Opacity, int X, int Y);

    private Frame _current;
    private Frame _from;
    private Frame _to;
    private double _duration;
    private double _elapsed;
    private Func<double, double> _easing = EaseOut;
    private Action? _onAnimationDone;
    private bool _animating;
    private TimeSpan _lastFrame;
    private nint _hwnd;

    /// <summary>The popup window is shown (fully, or still fading in or out).</summary>
    private bool _onScreen;

    /// <summary>Fading down before switching to <see cref="_app"/>.</summary>
    private bool _switching;

    /// <summary>Physical size of the content currently on screen.</summary>
    private PixelSize _shownSize;

    public WindowPreviewPopup(
        IWindowThumbnailService thumbnails,
        ICursorService cursor,
        IWindowChrome chrome,
        IWindowSwitcher switcher,
        IWindowTracker tracker,
        IMonitorService monitors,
        IUiDispatcher dispatcher,
        TaskbarTheme theme,
        ILogger logger)
    {
        _thumbnails = thumbnails;
        _cursor = cursor;
        _chrome = chrome;
        _switcher = switcher;
        _tracker = tracker;
        _monitors = monitors;
        _dispatcher = dispatcher;
        _theme = theme;
        _logger = logger;
        _showTimer = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(ShowDelayMs), OnShowTimer);
        _hideTimer = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(HideDelayMs), OnHideTimer);
        _watchTimer = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(WatchIntervalMs), OnWatchTimer);
    }

    /// <summary>The pointer entered an application button. <paramref name="anchor"/> is the button's column of the bar, in physical pixels.</summary>
    public void OnAppHovered(AppButtonViewModel app, ScreenRect anchor)
    {
        if (_disposed)
        {
            return;
        }

        _hideTimer.Stop();
        if (_onScreen)
        {
            // Moving along the bar while a preview is open (or still fading out) switches without the hover delay,
            // like the Windows taskbar.
            _showTimer.Stop();
            if (!_visible || !ReferenceEquals(app, _app))
            {
                ShowFor(app, anchor);
            }

            return;
        }

        _pendingApp = app;
        _pendingAnchor = anchor;
        _showTimer.Stop();
        _showTimer.Start();
    }

    /// <summary>The pointer left an application button.</summary>
    public void OnAppUnhovered(AppButtonViewModel app)
    {
        if (ReferenceEquals(app, _pendingApp))
        {
            _showTimer.Stop();
            _pendingApp = null;
        }

        if (_visible)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    /// <summary>Closes the preview: fading out, or at once when <paramref name="animate"/> is false.</summary>
    public void Hide(bool animate = true)
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _pendingApp = null;
        _switching = false;
        if (_visible)
        {
            _visible = false;
            _watchTimer.Stop();
            _tracker.WindowsChanged -= OnWindowsChanged;
            _grace = default;
        }

        if (animate && _onScreen && _hwnd != 0)
        {
            var sink = (int)Math.Round(SlideDip * _chrome.GetScale(_hwnd));
            Animate(new Frame(0, _current.X, _bounds.Top + sink), CloseMs * _current.Opacity, EaseIn, FinishHide);
        }
        else
        {
            FinishHide();
        }
    }

    /// <summary>Takes the popup off screen and releases its thumbnails, once it has faded out.</summary>
    private void FinishHide()
    {
        StopAnimation();
        _onScreen = false;
        _switching = false;
        _current = default;
        ClearThumbnails();
        _cards?.Children.Clear();
        _window?.AppWindow.Hide();
        _app = null;
        _shownApp = null;
    }

    private void Animate(Frame to, double durationMs, Func<double, double> easing, Action? onDone = null)
    {
        _from = _current;
        _to = to;
        _duration = Math.Max(1, durationMs);
        _elapsed = 0;
        _easing = easing;
        _onAnimationDone = onDone;
        if (!_animating)
        {
            _animating = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopAnimation()
    {
        _onAnimationDone = null;
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        var step = _lastFrame == TimeSpan.Zero || now <= _lastFrame ? 16.0 : (now - _lastFrame).TotalMilliseconds;
        _lastFrame = now;
        _elapsed += Math.Min(step, 50); // one stalled frame must not skip the whole animation

        var t = Math.Min(1, _elapsed / _duration);
        var k = _easing(t);
        _current = new Frame(
            _from.Opacity + (_to.Opacity - _from.Opacity) * k,
            (int)Math.Round(_from.X + (_to.X - _from.X) * k),
            (int)Math.Round(_from.Y + (_to.Y - _from.Y) * k));
        Apply(_current);

        if (t >= 1)
        {
            var done = _onAnimationDone;
            StopAnimation();
            done?.Invoke();
        }
    }

    /// <summary>Ease-out cubic: fast start, gentle landing.</summary>
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>Ease-in quadratic: gentle start, quick exit.</summary>
    private static double EaseIn(double t) => t * t;

    private void Apply(Frame frame)
    {
        if (_hwnd != 0)
        {
            _chrome.SetOpacity(_hwnd, frame.Opacity);
            _chrome.MoveTo(_hwnd, frame.X, frame.Y);
        }
    }

    private void OnShowTimer()
    {
        _showTimer.Stop();
        if (_pendingApp is { } app)
        {
            _pendingApp = null;
            ShowFor(app, _pendingAnchor);
        }
    }

    private void OnHideTimer()
    {
        _hideTimer.Stop();
        Hide();
    }

    /// <summary>
    /// Backs up the pointer events, which do not always arrive (e.g. when the pointer jumps, or the popup's content
    /// is rebuilt under it): the popup closes once the pointer is neither on it, on its button, nor in the gap between.
    /// </summary>
    private void OnWatchTimer()
    {
        if (!_visible || !_cursor.TryGetPosition(out var x, out var y))
        {
            return;
        }

        if (!_grace.IsEmpty && !_grace.Contains(x, y))
        {
            _grace = default;
        }

        var onPopup = _bounds.Contains(x, y) || _grace.Contains(x, y);
        var keepOpen = onPopup
                       || _anchor.Contains(x, y)
                       || (x >= _bounds.Left && x < _bounds.Right && y >= _bounds.Bottom && y < _anchor.Top);
        if (keepOpen)
        {
            if (onPopup)
            {
                _hideTimer.Stop();
            }
        }
        else if (!_hideTimer.IsRunning)
        {
            _hideTimer.Start();
        }
    }

    private void ShowFor(AppButtonViewModel app, ScreenRect anchor)
    {
        if (app.Windows.Count == 0)
        {
            Hide();
            return;
        }

        try
        {
            EnsureWindow();
            var previous = _shownApp;
            _app = app;
            _anchor = anchor;
            BeginShowing();

            if (!_onScreen)
            {
                Open();
            }
            else if (ReferenceEquals(previous, app) && !_switching)
            {
                // Hovered again while it was fading out: same content, just come back up.
                Animate(new Frame(1, _bounds.Left, _bounds.Top), OpenMs, EaseOut);
            }
            else if (!_switching)
            {
                _switching = true;
                Animate(_current with { Opacity = Math.Min(_current.Opacity, SwitchDipOpacity) }, SwitchOutMs, EaseIn, SwitchIn);
            }

            // Already fading down for a switch: SwitchIn picks up the latest _app when the fade-down ends.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the window preview");
            Hide(animate: false);
        }
    }

    private void BeginShowing()
    {
        if (!_visible)
        {
            _visible = true;
            _tracker.WindowsChanged += OnWindowsChanged;
            _watchTimer.Start();
        }
    }

    /// <summary>First appearance: fade in while rising into place.</summary>
    private void Open()
    {
        if (!BuildContent())
        {
            return;
        }

        var rise = (int)Math.Round(SlideDip * _chrome.GetScale(_hwnd));
        _current = new Frame(0, _bounds.Left, _bounds.Top + rise);
        Apply(_current); // transparent before it is shown, so the first visible frame is part of the animation
        _window!.AppWindow.MoveAndResize(new RectInt32(_current.X, _current.Y, _bounds.Width, _bounds.Height));
        _chrome.ShowWithoutActivation(_hwnd);
        _onScreen = true;
        FinishLayout();
        Animate(new Frame(1, _bounds.Left, _bounds.Top), OpenMs, EaseOut);
    }

    /// <summary>The fade-down of a switch ended: swap in the latest app and glide to its place.</summary>
    private void SwitchIn()
    {
        _switching = false;
        if (!_visible || _app is null)
        {
            return;
        }

        if (_app.Windows.Count == 0)
        {
            Hide();
            return;
        }

        // Keep the popup's center and bottom edge where they were, then move to the new place.
        var oldSize = _shownSize;
        var centerX = _current.X + oldSize.Width / 2;
        var bottom = _current.Y + oldSize.Height;
        if (!BuildContent())
        {
            return;
        }

        _current = _current with { X = centerX - _bounds.Width / 2, Y = bottom - _bounds.Height };
        _window!.AppWindow.MoveAndResize(new RectInt32(_current.X, _current.Y, _bounds.Width, _bounds.Height));
        Apply(_current);
        FinishLayout();
        Animate(new Frame(1, _bounds.Left, _bounds.Top), SwitchInMs, EaseOut);
    }

    /// <summary>The windows of the shown app changed (one opened, closed or was retitled): update in place.</summary>
    private void Refresh()
    {
        var animating = _animating;
        if (!BuildContent())
        {
            return;
        }

        if (animating)
        {
            // Let the running animation land on the new place.
            _from = _current;
            _to = _to with { X = _bounds.Left, Y = _bounds.Top };
            _window!.AppWindow.Resize(new SizeInt32(_bounds.Width, _bounds.Height));
        }
        else
        {
            _current = _current with { X = _bounds.Left, Y = _bounds.Top };
            _window!.AppWindow.MoveAndResize(new RectInt32(_bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height));
        }

        FinishLayout();
    }

    private void FinishLayout()
    {
        _root!.UpdateLayout();
        PositionThumbnails();
    }

    /// <summary>Builds the cards and thumbnails for <see cref="_app"/> and computes <see cref="_bounds"/>; does not move the window.</summary>
    private bool BuildContent()
    {
        if (_app is null || _window is null || _root is null || _cards is null)
        {
            return false;
        }

        ClearThumbnails();
        _cards.Children.Clear();
        var previousApp = _shownApp;
        _shownApp = _app;

        var hwnd = _hwnd;
        var scale = _chrome.GetScale(hwnd);
        var windows = _app.Windows.ToList();
        var active = _tracker.ActiveWindow;
        _snapshot = Snapshot(windows, active);

        var thumbnails = windows.Select(w => _thumbnails.Create(hwnd, w.Handle)).ToList();
        var monitor = _monitors.GetPrimaryMonitor().Bounds;
        var available = (monitor.Width - 2 * ScreenMargin * scale) / scale - 2 * OuterPadding;
        var slots = PreviewLayout.Fit(
            thumbnails.Select(t => t?.SourceSize ?? default).ToList(),
            MaxThumbnailWidth,
            MaxThumbnailHeight,
            MinContentWidth,
            2 * CardPadding,
            CardSpacing,
            available);
        var contentHeight = slots.Max(s => s.ThumbnailHeight);

        for (var i = 0; i < windows.Count; i++)
        {
            _cards.Children.Add(CreateCard(windows[i], windows[i].Handle == active, thumbnails[i], slots[i], contentHeight));
        }

        var widthDip = 2 * OuterPadding + slots.Sum(s => s.ContentWidth + 2 * CardPadding) + CardSpacing * (slots.Count - 1);
        var heightDip = 2 * OuterPadding + 2 * CardPadding + HeaderHeight + HeaderGap + contentHeight;
        _root.Width = widthDip;
        _root.Height = heightDip;
        _root.RequestedTheme = _theme.ElementTheme;
        _acrylic?.Update(_theme.ElementTheme, _theme.SurfaceOpaque);
        _window.SystemBackdrop = _theme.UseAcrylic ? _acrylic : null;

        var bounds = PreviewLayout.Place(
            (_anchor.Left + _anchor.Right) / 2,
            _anchor.Top,
            (int)Math.Ceiling(widthDip * scale),
            (int)Math.Ceiling(heightDip * scale),
            (int)Math.Round(GapAboveBar * scale),
            monitor,
            (int)Math.Round(ScreenMargin * scale));
        _grace = _onScreen && ReferenceEquals(previousApp, _app) ? _bounds.Union(_grace.IsEmpty ? _bounds : _grace) : default;
        _bounds = bounds;
        _shownSize = new PixelSize(bounds.Width, bounds.Height);
        return true;
    }

    private FrameworkElement CreateCard(TrackedWindow window, bool isActive, IWindowThumbnail? thumbnail, PreviewSlot slot, double contentHeight)
    {
        var foreground = Resource("SB.Foreground");
        var header = new Grid { Height = HeaderHeight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_app?.Icon is { } icon)
        {
            header.Children.Add(new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        }

        var title = new TextBlock
        {
            Text = window.Title,
            FontSize = 12,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var close = new Button
        {
            Style = _cardStyle,
            Width = HeaderHeight,
            Height = HeaderHeight,
            Padding = new Thickness(0),
            Background = Resource("SB.Transparent"),
            Opacity = 0,
            Content = new FontIcon { Glyph = "", FontSize = 10, Foreground = foreground },
        };
        AutomationProperties.SetName(close, "Close window");
        ToolTipService.SetToolTip(close, "Close window");
        close.Click += (_, _) => _switcher.Close(window.Handle);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        var placeholder = new Border
        {
            Width = slot.ThumbnailWidth,
            Height = slot.ThumbnailHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (thumbnail is null && _app?.Icon is { } fallback)
        {
            placeholder.Child = new Image { Source = fallback, Width = 32, Height = 32 };
        }

        var area = new Grid { Height = contentHeight, Margin = new Thickness(0, HeaderGap, 0, 0) };
        area.Children.Add(placeholder);

        var body = new StackPanel { Width = slot.ContentWidth };
        body.Children.Add(header);
        body.Children.Add(area);

        var card = new Button
        {
            Style = _cardStyle,
            Padding = new Thickness(CardPadding),
            Background = Resource(isActive ? "SB.ActiveItem" : "SB.Transparent"),
            Content = body,
        };
        AutomationProperties.SetName(card, window.Title);
        card.Click += (_, _) =>
        {
            _switcher.Activate(window.Handle);
            Hide();
        };
        card.PointerEntered += (_, _) => close.Opacity = 1;
        card.PointerExited += (_, _) => close.Opacity = 0;

        if (thumbnail is not null)
        {
            _shown.Add((placeholder, thumbnail));
        }

        return card;
    }

    private void PositionThumbnails()
    {
        if (_root?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var scale = xamlRoot.RasterizationScale;
        foreach (var (placeholder, thumbnail) in _shown)
        {
            if (placeholder.ActualWidth <= 0 || placeholder.XamlRoot is null)
            {
                continue;
            }

            var origin = placeholder.TransformToVisual(_root).TransformPoint(new Point(0, 0));
            var rect = ScreenRect.FromSize(
                (int)Math.Round(origin.X * scale),
                (int)Math.Round(origin.Y * scale),
                (int)Math.Round(placeholder.ActualWidth * scale),
                (int)Math.Round(placeholder.ActualHeight * scale));
            if (!rect.IsEmpty)
            {
                thumbnail.Show(rect);
            }
        }
    }

    private void OnWindowsChanged(object? sender, EventArgs e)
    {
        // Coalesced and deferred: the apps section rebuilds its buttons from the same event first.
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _refreshQueued = false;
            if (!_visible || _app is null)
            {
                return;
            }

            if (_switching)
            {
                return; // SwitchIn builds from the current windows anyway
            }

            if (_app.Windows.Count == 0)
            {
                Hide();
            }
            else if (Snapshot(_app.Windows, _tracker.ActiveWindow) != _snapshot)
            {
                Refresh();
            }
        });
    }

    private static string Snapshot(IEnumerable<TrackedWindow> windows, nint active) =>
        string.Join("|", windows.Select(w => $"{w.Handle}:{w.Title}")) + "|" + active;

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _cardStyle = (Style)XamlReader.Load(CardStyleXaml);
        _cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = CardSpacing };
        _root = new Border
        {
            Background = Resource("SB.Surface"),
            Padding = new Thickness(OuterPadding),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _cards,
        };
        _root.PointerEntered += (_, _) => _hideTimer.Stop();
        _root.PointerExited += (_, _) =>
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        };
        _root.SizeChanged += (_, _) => PositionThumbnails();

        var window = new Window { Title = "SuperBar Window Preview", Content = _root };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Closing += (_, args) => args.Cancel = !_disposed;

        var hwnd = WindowNative.GetWindowHandle(window);
        _hwnd = hwnd;
        _chrome.ApplyShellSurfaceStyle(hwnd, roundedCorners: true);
        _tracker.Ignore(hwnd);

        _acrylic = new TaskbarAcrylicBackdrop(_logger);
        window.Activated += (_, _) => _acrylic.KeepInputActive();
        _window = window;
    }

    private static Brush? Resource(string key) => Application.Current.Resources[key] as Brush;

    private void ClearThumbnails()
    {
        foreach (var (_, thumbnail) in _shown)
        {
            thumbnail.Dispose();
        }

        _shown.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide(animate: false);
        _disposed = true;
        _showTimer.Dispose();
        _hideTimer.Dispose();
        _watchTimer.Dispose();
        if (_window is not null)
        {
            _window.SystemBackdrop = null;
            _window.Close();
            _window = null;
        }
    }
}
