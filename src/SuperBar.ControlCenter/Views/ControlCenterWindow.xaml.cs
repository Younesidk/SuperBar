using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SuperBar.ControlCenter.ViewModels;
using SuperBar.Core.Shell;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace SuperBar.ControlCenter.Views;

/// <summary>
/// The separate settings window: Mica, a left navigation pane with a status footer, and one page at a time. Each page
/// template is rooted in a <see cref="PageContainer"/>, which owns the page's header, scrolling and centered column.
/// </summary>
public sealed partial class ControlCenterWindow : Window
{
    // Comfortable without maximizing; still below Breakpoints.WideMinWidth, so the window opens in the compact layout.
    private const int WidthDip = 980;
    private const int HeightDip = 680;

    // The smallest size at which the pane (232) plus a single-column page stays usable.
    private const int MinWidthDip = 760;
    private const int MinHeightDip = 540;

    public ControlCenterWindow(ControlCenterViewModel viewModel, IWindowChrome chrome)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ConfigureBackdrop();
        ConfigureWindow(chrome.GetScale(WindowNative.GetWindowHandle(this)));

        Root.Loaded += (_, _) => Navigation.SelectedItem = ViewModel.SelectedPage;
        Closed += (_, _) => ViewModel.Dispose();
    }

    public ControlCenterViewModel ViewModel { get; }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is PageViewModel page)
        {
            ViewModel.SelectedPage = page;
        }
    }

    private void ConfigureBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        else
        {
            // Windows 10 (or Mica unavailable): the previous solid dark background.
            Root.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x15, 0x17, 0x1C));
        }
    }

    private void ConfigureWindow(double scale)
    {
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinWidthDip * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinHeightDip * scale);
        }

        // AppWindow works in physical pixels: scale the DIP size, then center on the window's display work area.
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));
    }
}

/// <summary>Picks each Control Center page's template (WinUI has no implicit, type-keyed DataTemplates).</summary>
public sealed partial class ControlCenterPageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? General { get; set; }
    public DataTemplate? Appearance { get; set; }
    public DataTemplate? Layout { get; set; }
    public DataTemplate? Widgets { get; set; }
    public DataTemplate? Applications { get; set; }
    public DataTemplate? About { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        GeneralPageViewModel => General,
        AppearancePageViewModel => Appearance,
        LayoutPageViewModel => Layout,
        WidgetsPageViewModel => Widgets,
        ApplicationsPageViewModel => Applications,
        AboutPageViewModel => About,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Picks the editor template of each generated settings field.</summary>
public sealed partial class SettingFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Toggle { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Number { get; set; }
    public DataTemplate? Text { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ToggleSettingViewModel => Toggle,
        ChoiceSettingViewModel => Choice,
        NumberSettingViewModel => Number,
        TextSettingViewModel => Text,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
