using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SuperBar.Core.Settings;

namespace SuperBar.Runtime.Views;

/// <summary>
/// The one and only taskbar window. Its position and size are owned by the AppBar registration
/// (physical pixels via SetWindowPos); this class only renders and themes the surface.
/// </summary>
public partial class TaskbarWindow : Window
{
    public TaskbarWindow()
    {
        InitializeComponent();
    }

    /// <summary>The bar refuses Alt+F4 and other close requests unless the runtime is shutting it down.</summary>
    public bool AllowClose { get; set; }

    public nint Handle => TryGetPlatformHandle()?.Handle ?? 0;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    public void ApplyAppearance(AppearanceSettings a)
    {
        var dark = a.Theme == ThemeMode.Dark;
        var accent = Color.TryParse(a.AccentColor, out var parsed) ? parsed : Color.Parse("#5B8CFF");
        var alpha = a.UseAcrylic ? (byte)Math.Round(a.BackgroundOpacity * 255) : (byte)255;

        TransparencyLevelHint = a.UseAcrylic
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];

        var surface = dark ? Color.FromArgb(alpha, 0x1A, 0x1D, 0x24) : Color.FromArgb(alpha, 0xF3, 0xF4, 0xF7);
        var ink = dark ? Color.FromRgb(0xF2, 0xF4, 0xF8) : Color.FromRgb(0x14, 0x17, 0x1F);
        var subtle = dark ? Color.FromRgb(0x9A, 0xA3, 0xB2) : Color.FromRgb(0x5C, 0x64, 0x73);

        Set("SB.Surface", new SolidColorBrush(surface));
        Set("SB.SurfaceBorder", new SolidColorBrush(WithAlpha(ink, 0x22)));
        Set("SB.Foreground", new SolidColorBrush(ink));
        Set("SB.Subtle", new SolidColorBrush(subtle));
        Set("SB.Hover", new SolidColorBrush(WithAlpha(ink, 0x17)));
        Set("SB.Pressed", new SolidColorBrush(WithAlpha(ink, 0x0F)));
        Set("SB.ActiveItem", new SolidColorBrush(WithAlpha(ink, 0x1F)));
        Set("SB.WidgetBackground", new SolidColorBrush(WithAlpha(ink, 0x12)));
        Set("SB.Track", new SolidColorBrush(WithAlpha(ink, 0x26)));
        Set("SB.Accent", new SolidColorBrush(accent));
        Set("SB.ItemCornerRadius", new CornerRadius(a.ItemCornerRadius));

        var itemSize = Math.Max(24, a.Height - 12);
        Set("SB.ItemSize", itemSize);
        Set("SB.IconSize", Math.Clamp(Math.Round(itemSize * 0.62), 16, 40));
        Set("SB.ItemSpacing", a.ItemSpacing);
        RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    private void Set(string key, object value) => Resources[key] = value;

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
