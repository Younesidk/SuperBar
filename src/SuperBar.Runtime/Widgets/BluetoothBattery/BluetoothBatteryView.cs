using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets.BuiltIn;
using Windows.UI;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// View of <see cref="BluetoothBatteryWidget"/> on the bar: a bar button with one "[icon] 85%" per shown device and,
/// when devices are left out, a small "+N" badge. A click toggles the flyout. Low and critical levels tint the icon
/// and the percentage (see <see cref="BatteryBrushes"/>).
/// </summary>
[WidgetViewFor(typeof(BluetoothBatteryModel))]
public sealed partial class BluetoothBatteryView : WidgetViewBase
{
    private readonly BluetoothBatteryModel _model;
    private readonly TaskbarTheme _theme;
    private readonly BatteryBrushes _brushes;
    private readonly StackPanel _items;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly FontIcon _emptyIcon;
    private readonly Button _button;
    private IReadOnlyList<BluetoothBatteryItem>? _shownItems;

    public BluetoothBatteryView(BluetoothBatteryModel model, TaskbarTheme theme)
        : base(model, theme)
    {
        _model = model;
        _theme = theme;
        _brushes = new BatteryBrushes(theme);
        _brushes.Update();

        _items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _badgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = theme.Subtle, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border
        {
            Background = theme.Track,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 0, 5, 1),
            MinHeight = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _badgeText,
        };
        _emptyIcon = new FontIcon { Glyph = BluetoothBatteryItem.GlyphFor(Core.Shell.BluetoothDeviceKind.Unknown), FontSize = 13, Foreground = theme.Subtle };

        _button = new Button
        {
            Command = model.Open,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _emptyIcon, _items, _badge },
            },
        };
        if (theme.FindStyle(TaskbarTheme.WideButtonStyleKey) is { } style)
        {
            _button.Style = style;
        }

        AutomationProperties.SetName(_button, "Bluetooth battery");
        Content = _button;
        Refresh();
    }

    protected override void Refresh()
    {
        _brushes.Update(); // in place: rows already drawn pick the new shade up
        if (!ReferenceEquals(_shownItems, _model.Items))
        {
            _shownItems = _model.Items;
            _items.Children.Clear();
            foreach (var item in _model.Items)
            {
                _items.Children.Add(Item(item));
            }
        }

        _items.Visibility = _model.HasDevices ? Visibility.Visible : Visibility.Collapsed;
        _emptyIcon.Visibility = _model.HasDevices ? Visibility.Collapsed : Visibility.Visible;
        _badgeText.Text = _model.HiddenText;
        _badge.Visibility = _model.HiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_button, _model.ToolTip);
    }

    private StackPanel Item(BluetoothBatteryItem item)
    {
        var brush = _brushes.For(item.Level);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = item.Glyph, FontSize = 13, Foreground = brush ?? _theme.Foreground },
                new TextBlock
                {
                    Text = item.PercentText,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = brush ?? _theme.Foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        AutomationProperties.SetName(panel, $"{item.Name} {item.PercentText}");
        return panel;
    }
}

/// <summary>
/// View of the <see cref="BluetoothBatteryWidget"/> flyout: a header with a refresh button, then one row per device
/// with its icon, name, percentage and a thin battery bar.
/// </summary>
[WidgetViewFor(typeof(BluetoothBatteryFlyoutModel))]
public sealed partial class BluetoothBatteryFlyoutView : WidgetViewBase
{
    private const double FlyoutWidth = 248;

    private readonly BluetoothBatteryFlyoutModel _model;
    private readonly TaskbarTheme _theme;
    private readonly BatteryBrushes _brushes;
    private readonly StackPanel _rows;
    private readonly TextBlock _empty;
    private readonly Button _refresh;
    private IReadOnlyList<BluetoothBatteryItem>? _shownDevices;

    public BluetoothBatteryFlyoutView(BluetoothBatteryFlyoutModel model, TaskbarTheme theme)
        : base(model, theme)
    {
        _model = model;
        _theme = theme;
        _brushes = new BatteryBrushes(theme);
        _brushes.Update();
        Width = FlyoutWidth;

        var title = new TextBlock
        {
            Text = "Bluetooth devices",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _refresh = new Button
        {
            Command = model.Refresh,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = theme.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new FontIcon { Glyph = "", FontSize = 12, Foreground = theme.Subtle },
        };
        AutomationProperties.SetName(_refresh, "Refresh");
        ToolTipService.SetToolTip(_refresh, "Refresh");
        Grid.SetColumn(_refresh, 1);
        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { title, _refresh },
        };

        _rows = new StackPanel { Spacing = 12 };
        _empty = new TextBlock
        {
            Text = "No connected device reports its battery.",
            FontSize = 12,
            Foreground = theme.Subtle,
            TextWrapping = TextWrapping.Wrap,
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { header, _rows, _empty },
        };
        Refresh();
    }

    protected override void Refresh()
    {
        _brushes.Update(); // in place: rows already drawn pick the new shade up
        if (!ReferenceEquals(_shownDevices, _model.Devices))
        {
            _shownDevices = _model.Devices;
            _rows.Children.Clear();
            foreach (var device in _model.Devices)
            {
                _rows.Children.Add(Row(device));
            }
        }

        _empty.Visibility = _model.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _refresh.Opacity = _model.IsRefreshing ? 0.4 : 1;
    }

    private StackPanel Row(BluetoothBatteryItem device)
    {
        var brush = _brushes.For(device.Level);
        var icon = new FontIcon { Glyph = device.Glyph, FontSize = 16, Foreground = brush ?? _theme.Foreground, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = 13,
            Foreground = _theme.Foreground,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(name, device.Name);
        Grid.SetColumn(name, 1);
        var percent = new TextBlock
        {
            Text = device.PercentText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush ?? _theme.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percent, 2);

        var track = new Border { Background = _theme.Track, CornerRadius = new CornerRadius(1.5) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Background = brush ?? _theme.Foreground, CornerRadius = new CornerRadius(1.5) };
        var bar = new Grid
        {
            Height = 3,
            Margin = new Thickness(26, 0, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(device.Fraction, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1 - device.Fraction, GridUnitType.Star) },
            },
            Children = { track, fill },
        };

        var row = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Grid
                {
                    ColumnSpacing = 10,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(16) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    Children = { icon, name, percent },
                },
                bar,
            },
        };
        AutomationProperties.SetName(row, $"{device.Name} {device.PercentText}");
        return row;
    }
}

/// <summary>
/// The amber and red of low and critical battery levels, in a shade that reads on the bar's current theme. Normal
/// levels use the theme's foreground (null here).
/// </summary>
internal sealed class BatteryBrushes(TaskbarTheme theme)
{
    private static readonly Color DarkLow = Color.FromArgb(0xFF, 0xFF, 0xB9, 0x3D);
    private static readonly Color LightLow = Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00);
    private static readonly Color DarkCritical = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);
    private static readonly Color LightCritical = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);

    private readonly SolidColorBrush _low = new();
    private readonly SolidColorBrush _critical = new();
    private ElementTheme? _for;

    public Brush? For(BatteryLevel level) =>
        level switch
        {
            BatteryLevel.Low => _low,
            BatteryLevel.Critical => _critical,
            _ => null,
        };

    /// <summary>Follows the theme's light/dark mode.</summary>
    public void Update()
    {
        if (_for == theme.ElementTheme)
        {
            return;
        }

        _for = theme.ElementTheme;
        var dark = theme.ElementTheme != ElementTheme.Light;
        _low.Color = dark ? DarkLow : LightLow;
        _critical.Color = dark ? DarkCritical : LightCritical;
    }
}
