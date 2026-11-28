using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SuperBar.Runtime.Theming;
using SuperBar.Widgets.BuiltIn;
using Windows.Storage.Streams;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// View of <see cref="MediaWidget"/>: one pill with rounded-square artwork, title over artist over a 3 px progress
/// bar, a round Play/Pause button and a borderless Next button. The widget draws its own pill (the shared card has
/// no border), using the shared theme brushes so theme and accent changes apply live.
/// </summary>
[WidgetViewFor(typeof(MediaModel))]
public sealed partial class MediaView : WidgetViewBase
{
    private const double ArtSize = 30;
    private const double ButtonSize = 28;

    private readonly MediaModel _model;
    private readonly TaskbarTheme _theme;
    private readonly ILogger<MediaView> _logger;
    private readonly Border _pill;
    private readonly Border _art;
    private readonly FontIcon _artFallback;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly Grid _progress;
    private readonly ColumnDefinition _played = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _remaining = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly Button _playPause;
    private readonly FontIcon _playPauseIcon;
    private readonly Button _next;
    private string? _shownArtworkKey;

    public MediaView(MediaModel model, TaskbarTheme theme, ILogger<MediaView> logger)
        : base(model, theme)
    {
        _model = model;
        _theme = theme;
        _logger = logger;

        _artFallback = new FontIcon { Glyph = "\uE8D6", FontSize = 14, Foreground = theme.Subtle };
        _art = new Border
        {
            Width = ArtSize,
            Height = ArtSize,
            CornerRadius = new CornerRadius(6),
            Background = theme.Track,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _artFallback,
        };

        _title = Line(12, FontWeights.SemiBold, theme.Foreground);
        _artist = Line(11, FontWeights.Normal, theme.Subtle);

        var track = new Border { Background = theme.Track, CornerRadius = new CornerRadius(1.5) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Background = theme.Foreground, CornerRadius = new CornerRadius(1.5) };
        _progress = new Grid
        {
            Height = 3,
            Margin = new Thickness(0, 4, 0, 0),
            ColumnDefinitions = { _played, _remaining },
            Children = { track, fill },
        };

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _title, _artist, _progress },
        };
        Grid.SetColumn(text, 1);

        _playPauseIcon = new FontIcon { FontSize = 11, Foreground = theme.Foreground };
        _playPause = RoundButton(model.PlayPause, theme.Hover, _playPauseIcon);
        _next = RoundButton(model.Next, theme.Transparent, new FontIcon { Glyph = "\uE893", FontSize = 11, Foreground = theme.Foreground });
        AutomationProperties.SetName(_next, "Next track");
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _playPause, _next },
        };
        Grid.SetColumn(controls, 2);

        _pill = new Border
        {
            Background = theme.WidgetBackground,
            BorderBrush = theme.SurfaceBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0, 4, 0),
            Child = new Grid
            {
                ColumnSpacing = 9,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Children = { _art, text, controls },
            },
        };
        Content = _pill;
        Refresh();
    }

    protected override void Refresh()
    {
        _pill.CornerRadius = _theme.ItemCornerRadius;
        _title.Text = _model.Title;
        _artist.Text = _model.Artist;
        _artist.Visibility = _model.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = _model.HasProgress ? Visibility.Visible : Visibility.Collapsed;
        _played.Width = new GridLength(_model.Progress, GridUnitType.Star);
        _remaining.Width = new GridLength(1 - _model.Progress, GridUnitType.Star);
        _playPauseIcon.Glyph = _model.IsPlaying ? "\uE769" : "\uE768";
        AutomationProperties.SetName(_playPause, _model.IsPlaying ? "Pause" : "Play");
        ToolTipService.SetToolTip(this, _model.ToolTip);

        if (_model.ThumbnailKey != _shownArtworkKey)
        {
            _shownArtworkKey = _model.ThumbnailKey;
            _ = ShowArtworkAsync(_model.Thumbnail, _model.ThumbnailKey);
        }
    }

    private async Task ShowArtworkAsync(byte[]? bytes, string? key)
    {
        if (bytes is null)
        {
            ShowFallbackArt();
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Logical };
            await bitmap.SetSourceAsync(stream);
            if (key != _shownArtworkKey)
            {
                return; // the track changed while decoding
            }

            _art.Child = null;
            _art.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not decode the media artwork");
            if (key == _shownArtworkKey)
            {
                ShowFallbackArt();
            }
        }
    }

    private void ShowFallbackArt()
    {
        _art.Background = _theme.Track;
        _art.Child = _artFallback;
    }

    private static TextBlock Line(double size, Windows.UI.Text.FontWeight weight, Brush foreground) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        MaxLines = 1,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Button RoundButton(System.Windows.Input.ICommand command, Brush background, UIElement icon) => new()
    {
        Command = command,
        Width = ButtonSize,
        Height = ButtonSize,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(ButtonSize / 2),
        Background = background,
        BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Content = icon,
    };
}
