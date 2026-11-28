using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SuperBar.Core.Shell;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace SuperBar.Runtime.Views;

/// <summary>
/// The tray's battery, drawn as vectors in the manner of Windows 11's colored battery icon: a rounded shell, a terminal
/// nub on the right, an inner bar whose width follows the exact percentage, and a lightning bolt while charging.
/// <para>
/// The fill color follows <see cref="TrayIndicators.FillFor"/>: green on AC power, red when critical, orange with
/// energy saver on, the foreground otherwise. The shell and the normal fill use <see cref="Control.Foreground"/> (the
/// bar's live foreground brush); the colored shades follow the element's light or dark theme.
/// </para>
/// <para>
/// Outline and padding are snapped to whole device pixels at the current rasterization scale (and re-snapped when the
/// window moves to a monitor with another scale), so the 1.5-DIP outline stays crisp at 100, 125, 150 and 200 % rather
/// than smearing across two pixel rows.
/// </para>
/// </summary>
public sealed partial class BatteryIcon : UserControl
{
    // Geometry in DIPs: a 20 x 12 shell, a 1 DIP gap, a 2 x 5 nub; 23 x 12 in all.
    private const double ShellWidth = 20;
    private const double ShellHeight = 12;
    private const double Outline = 1.5;
    private const double Inset = 1.5;
    private const double NubWidth = 2;
    private const double NubHeight = 5;
    private const double NubGap = 1;

    /// <summary>The narrowest fill drawn for any level above 0 %, so a few percent never looks empty.</summary>
    private const double MinimumSliver = 2;

    private static readonly Color DarkGreen = Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F);
    private static readonly Color LightGreen = Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F);
    private static readonly Color DarkRed = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);
    private static readonly Color LightRed = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);
    private static readonly Color DarkOrange = Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00);
    private static readonly Color LightOrange = Color.FromArgb(0xFF, 0xCA, 0x50, 0x10);

    /// <summary>The bar's opaque surface colors (TaskbarTheme): the gap cut around the bolt.</summary>
    private static readonly Color DarkCutout = Color.FromArgb(0xFF, 0x1A, 0x1D, 0x24);
    private static readonly Color LightCutout = Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF7);

    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage), typeof(double), typeof(BatteryIcon), new PropertyMetadata(0d, OnStateChanged));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsPluggedInProperty = DependencyProperty.Register(
        nameof(IsPluggedIn), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsEnergySaverOnProperty = DependencyProperty.Register(
        nameof(IsEnergySaverOn), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, OnStateChanged));

    private readonly Border _shell;
    private readonly Border _fill;
    private readonly Border _nub;
    private readonly Path _bolt;
    private readonly SolidColorBrush _fillColor = new();
    private readonly SolidColorBrush _outlineColor = new();
    private readonly SolidColorBrush _boltCutout = new();
    private XamlRoot? _root;
    private double _scale = 1;

    public BatteryIcon()
    {
        UseLayoutRounding = true;
        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;

        _fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5) };
        _shell = new Border
        {
            Width = ShellWidth,
            Height = ShellHeight,
            CornerRadius = new CornerRadius(3),
            Child = _fill,
        };
        _nub = new Border
        {
            Width = NubWidth,
            Height = NubHeight,
            CornerRadius = new CornerRadius(0, 1.5, 1.5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(NubGap, 0, 0, 0),
        };
        Grid.SetColumn(_nub, 1);

        _bolt = new Path
        {
            Data = BoltGeometry(),
            Stroke = _boltCutout,
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        // The bolt shares the shell's column, so it centers on the battery body, not on body + nub.
        Content = new Grid
        {
            UseLayoutRounding = true,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { _shell, _nub, _bolt },
        };

        ActualThemeChanged += (_, _) => Update();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Update());
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Snap();
        Update();
    }

    /// <summary>0..100. Values in between draw a proportionally wide fill.</summary>
    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    /// <summary>Taking charge right now: green fill and a lightning bolt.</summary>
    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    /// <summary>On AC power (charging, full, or held at a charge limit): green fill.</summary>
    public bool IsPluggedIn
    {
        get => (bool)GetValue(IsPluggedInProperty);
        set => SetValue(IsPluggedInProperty, value);
    }

    /// <summary>Energy saver on (and not on AC power): orange fill, unless the level is critical.</summary>
    public bool IsEnergySaverOn
    {
        get => (bool)GetValue(IsEnergySaverOnProperty);
        set => SetValue(IsEnergySaverOnProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BatteryIcon)d).Update();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_root, XamlRoot))
        {
            DetachRoot();
            _root = XamlRoot;
            if (_root is not null)
            {
                _root.Changed += OnRootChanged;
            }
        }

        Snap();
        Update();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A stale Unloaded can follow the Loaded of a re-parented element: keep listening while still in the tree.
        if (!IsLoaded)
        {
            DetachRoot();
        }
    }

    private void DetachRoot()
    {
        if (_root is not null)
        {
            _root.Changed -= OnRootChanged;
            _root = null;
        }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _scale) > 0.001)
        {
            Snap();
            Update();
        }
    }

    /// <summary>Outline and inset to whole device pixels: at least one pixel each, the outline rounded, the inset rounded down.</summary>
    private void Snap()
    {
        _scale = XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 1;
        var outline = Math.Max(1, Math.Round(Outline * _scale, MidpointRounding.AwayFromZero)) / _scale;
        var inset = Math.Max(1, Math.Floor(Inset * _scale)) / _scale;
        _shell.BorderThickness = new Thickness(outline);
        _shell.Padding = new Thickness(inset);
    }

    private void Update()
    {
        var percent = Math.Clamp(Percentage, 0, 100);
        var fill = TrayIndicators.FillFor((int)Math.Round(percent), IsCharging, IsPluggedIn, IsEnergySaverOn);
        var dark = ActualTheme != ElementTheme.Light;
        var foreground = (Foreground as SolidColorBrush)?.Color ?? (dark ? Colors.White : Colors.Black);

        // Normal: the live foreground brush itself, so a theme switch recolors it at once.
        if (fill == BatteryFill.Normal)
        {
            _fill.Background = Foreground;
        }
        else
        {
            _fillColor.Color = fill switch
            {
                BatteryFill.PluggedIn => dark ? DarkGreen : LightGreen,
                BatteryFill.Critical => dark ? DarkRed : LightRed,
                _ => dark ? DarkOrange : LightOrange,
            };
            _fill.Background = _fillColor;
        }

        // Critical also tints the outline, halfway to red: unmistakable without turning the whole icon into a blob.
        if (fill == BatteryFill.Critical)
        {
            _outlineColor.Color = Mix(foreground, _fillColor.Color, 0.5);
            _shell.BorderBrush = _outlineColor;
            _nub.Background = _outlineColor;
        }
        else
        {
            _shell.BorderBrush = Foreground;
            _nub.Background = Foreground;
        }

        var inner = ShellWidth - (_shell.BorderThickness.Left + _shell.Padding.Left) * 2;
        _fill.Width = percent <= 0 ? 0 : Math.Max(MinimumSliver, inner * percent / 100);
        _fill.Visibility = percent <= 0 ? Visibility.Collapsed : Visibility.Visible;

        // The bolt in the foreground with a cutout in the bar's surface color around it: readable over the colored fill
        // and over the empty part of the shell alike.
        _bolt.Visibility = IsCharging ? Visibility.Visible : Visibility.Collapsed;
        _bolt.Fill = Foreground;
        _boltCutout.Color = dark ? DarkCutout : LightCutout;
    }

    /// <summary>A lightning bolt, 5.2 x 8 DIPs.</summary>
    private static PathGeometry BoltGeometry()
    {
        var figure = new PathFigure { StartPoint = new Point(3.4, 0), IsClosed = true, IsFilled = true };
        foreach (var (x, y) in new[] { (0.4, 4.6), (2.6, 4.6), (2.0, 8.0), (5.2, 3.3), (2.9, 3.3) })
        {
            figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        0xFF,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
