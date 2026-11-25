using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using Windows.UI;

namespace SuperBar.Runtime.Theming;

/// <summary>
/// The live look of the taskbar, derived from <see cref="AppearanceSettings"/>.
/// <para>
/// WinUI has no <c>{DynamicResource}</c>: replacing a resource does not update elements that already use it.
/// So every brush here is created ONCE and only its <see cref="SolidColorBrush.Color"/> is mutated, which
/// updates every element using it immediately. Numeric metrics are observable properties that the XAML binds to
/// with <c>{Binding X, Source={StaticResource SB.Theme}}</c>.
/// </para>
/// Must be created and used on the UI thread.
/// </summary>
public sealed class TaskbarTheme : ObservableObject
{
    public const string ResourceKey = "SB.Theme";

    /// <summary>Key of the bar's padded item button style (hover, pressed and press-scale states).</summary>
    public const string WideButtonStyleKey = "SbWideButton";

    private static readonly ArgbColor DefaultAccent = new(0xFF, 0x5B, 0x8C, 0xFF);
    private static readonly ArgbColor DarkSurface = new(0xFF, 0x1A, 0x1D, 0x24);
    private static readonly ArgbColor LightSurface = new(0xFF, 0xF3, 0xF4, 0xF7);
    private static readonly ArgbColor DarkInk = new(0xFF, 0xF2, 0xF4, 0xF8);
    private static readonly ArgbColor LightInk = new(0xFF, 0x14, 0x17, 0x1F);
    private static readonly ArgbColor DarkSubtle = new(0xFF, 0x9A, 0xA3, 0xB2);
    private static readonly ArgbColor LightSubtle = new(0xFF, 0x5C, 0x64, 0x73);

    private double _itemSize = 40;
    private double _iconSize = 26;
    private double _itemSpacing = 6;
    private CornerRadius _itemCornerRadius = new(10);
    private Thickness _surfacePadding = new(8, 0, 8, 0);
    private ElementTheme _elementTheme = ElementTheme.Dark;
    private bool _useAcrylic = true;
    private Color _surfaceOpaque = ToColor(DarkSurface);
    private bool _registered;
    private ResourceDictionary? _barStyles;

    public TaskbarTheme()
    {
        Apply(new AppearanceSettings());
    }

    // --- Brushes (stable instances; colors change in place) -------------------------------------------------

    public SolidColorBrush Surface { get; } = new();
    public SolidColorBrush SurfaceBorder { get; } = new();
    public SolidColorBrush Foreground { get; } = new();
    public SolidColorBrush Subtle { get; } = new();
    public SolidColorBrush Hover { get; } = new();
    public SolidColorBrush Pressed { get; } = new();
    public SolidColorBrush ActiveItem { get; } = new();
    public SolidColorBrush WidgetBackground { get; } = new();
    public SolidColorBrush Track { get; } = new();
    public SolidColorBrush Accent { get; } = new();
    public SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));

    // --- Metrics ----------------------------------------------------------------------------------------------

    public double ItemSize { get => _itemSize; private set => SetProperty(ref _itemSize, value); }

    public double IconSize { get => _iconSize; private set => SetProperty(ref _iconSize, value); }

    public double ItemSpacing { get => _itemSpacing; private set => SetProperty(ref _itemSpacing, value); }

    public CornerRadius ItemCornerRadius { get => _itemCornerRadius; private set => SetProperty(ref _itemCornerRadius, value); }

    public Thickness SurfacePadding { get => _surfacePadding; private set => SetProperty(ref _surfacePadding, value); }

    // --- Window-level appearance (consumed by TaskbarWindow) ---------------------------------------------------

    public ElementTheme ElementTheme { get => _elementTheme; private set => SetProperty(ref _elementTheme, value); }

    public bool UseAcrylic { get => _useAcrylic; private set => SetProperty(ref _useAcrylic, value); }

    /// <summary>The surface color without transparency: acrylic tint and fallback color.</summary>
    public Color SurfaceOpaque { get => _surfaceOpaque; private set => SetProperty(ref _surfaceOpaque, value); }

    public void Apply(AppearanceSettings a)
    {
        var dark = a.Theme == ThemeMode.Dark;
        var accent = ColorHex.ParseOrDefault(a.AccentColor, DefaultAccent);
        var alpha = a.UseAcrylic ? (byte)Math.Round(Math.Clamp(a.BackgroundOpacity, 0, 1) * 255) : (byte)255;
        var surface = dark ? DarkSurface : LightSurface;
        var ink = dark ? DarkInk : LightInk;

        Surface.Color = ToColor(surface.WithAlpha(alpha));
        SurfaceBorder.Color = ToColor(ink.WithAlpha(0x22));
        Foreground.Color = ToColor(ink);
        Subtle.Color = ToColor(dark ? DarkSubtle : LightSubtle);
        Hover.Color = ToColor(ink.WithAlpha(0x17));
        Pressed.Color = ToColor(ink.WithAlpha(0x0F));
        ActiveItem.Color = ToColor(ink.WithAlpha(0x1F));
        WidgetBackground.Color = ToColor(ink.WithAlpha(0x12));
        Track.Color = ToColor(ink.WithAlpha(0x26));
        Accent.Color = ToColor(accent.WithAlpha(0xFF));

        var itemSize = Math.Max(24, a.Height - 12);
        ItemSize = itemSize;
        IconSize = Math.Clamp(Math.Round(itemSize * 0.62), 16, 40);
        ItemSpacing = a.ItemSpacing;
        ItemCornerRadius = new CornerRadius(a.ItemCornerRadius);
        SurfacePadding = new Thickness(a.EdgePadding, 0, a.EdgePadding, 0);

        SurfaceOpaque = ToColor(surface);
        UseAcrylic = a.UseAcrylic;
        ElementTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    /// <summary>
    /// Publishes the theme and its brushes (<c>SB.Theme</c>, <c>SB.Surface</c>, <c>SB.Accent</c>, …) so XAML can use
    /// <c>{StaticResource}</c>. Must run before any XAML that references them is loaded. Idempotent.
    /// </summary>
    public void RegisterResources(ResourceDictionary resources)
    {
        if (_registered)
        {
            return;
        }

        resources[ResourceKey] = this;
        resources["SB.Surface"] = Surface;
        resources["SB.SurfaceBorder"] = SurfaceBorder;
        resources["SB.Foreground"] = Foreground;
        resources["SB.Subtle"] = Subtle;
        resources["SB.Hover"] = Hover;
        resources["SB.Pressed"] = Pressed;
        resources["SB.ActiveItem"] = ActiveItem;
        resources["SB.WidgetBackground"] = WidgetBackground;
        resources["SB.Track"] = Track;
        resources["SB.Accent"] = Accent;
        resources["SB.Transparent"] = Transparent;
        _registered = true;
    }

    /// <summary>
    /// Publishes the named styles of the bar surface (<c>SbItemButton</c>, <c>SbWideButton</c>, …) so widget views,
    /// which are built in code, can look like the bar's own items. Called by the taskbar window once its XAML loaded.
    /// </summary>
    public void RegisterStyles(ResourceDictionary barResources) => _barStyles = barResources;

    /// <summary>A style of the bar surface, or null when the bar has not been created yet or has no such style.</summary>
    public Style? FindStyle(string key) =>
        _barStyles is not null && _barStyles.TryGetValue(key, out var value) ? value as Style : null;

    private static Color ToColor(ArgbColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
