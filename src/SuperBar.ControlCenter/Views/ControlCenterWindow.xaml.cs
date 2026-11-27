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

    public ControlCenterWindow(ControlCenterViewModel viewModel, IWindowChrome chrome, WindowControlCenterUi ui)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ui.Attach(this, Root);

        ConfigureBackdrop();
        ConfigureWindow(chrome.GetScale(WindowNative.GetWindowHandle(this)));

        // The pages live exactly as long as this window, so there is nothing to unsubscribe.
        foreach (var widgets in ViewModel.Pages.OfType<WidgetsPageViewModel>())
        {
            widgets.TileFocusRequested += OnWidgetTileFocusRequested;
        }

        Root.Loaded += (_, _) => Navigation.SelectedItem = ViewModel.SelectedPage;
        Closed += (_, _) => ViewModel.Dispose();
    }

    public ControlCenterViewModel ViewModel { get; }

    // Widgets page: the open catalog flyout and the tile grid of the page template's current instance.
    private Flyout? _widgetCatalog;
    private ItemsControl? _widgetTiles;

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is PageViewModel page)
        {
            ViewModel.SelectedPage = page;
        }
    }

    /// <summary>
    /// Fills the Applications page's "Pin application" menu each time it opens: the running apps that can be pinned,
    /// then "Browse". Built in code because a MenuFlyout has no ItemsSource; a MenuFlyout because it closes itself when
    /// an item is chosen.
    /// </summary>
    private void OnPinFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || ViewModel.SelectedPage is not ApplicationsPageViewModel page)
        {
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem { Text = "Running applications", IsEnabled = false });

        var running = page.GetRunningApps();
        foreach (var app in running)
        {
            var item = new MenuFlyoutItem { Text = app.DisplayName };
            if (app.Icon is { } icon)
            {
                item.Icon = new ImageIcon { Source = icon };
            }

            ToolTipService.SetToolTip(item, app.Path);
            var path = app.Path;
            item.Click += (_, _) => page.Pin(path);
            menu.Items.Add(item);
        }

        if (running.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No running applications to pin", IsEnabled = false });
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var browse = new MenuFlyoutItem { Text = "Browse for an application...", Icon = new FontIcon { Glyph = "\uE8E5" } };
        browse.Click += (_, _) => page.BrowseAndPin();
        menu.Items.Add(browse);
    }

    /// <summary>
    /// Layout page: a move re-creates the moved item's row (in its new place or zone), which drops keyboard focus.
    /// When the row's button that started the move is loaded again, it takes the focus back.
    /// </summary>
    private void OnLayoutItemButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LayoutItemViewModel item, Tag: string tag } button
            && Enum.TryParse<LayoutFocusTarget>(tag, out var target)
            && item.TakeFocusRequest(target))
        {
            button.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Widgets page: remembers the catalog flyout while it is open, so an Add button can close it.</summary>
    private void OnWidgetCatalogOpening(object? sender, object e) => _widgetCatalog = sender as Flyout;

    /// <summary>
    /// Widgets page: a Flyout stays open when something inside is clicked. Adding a widget closes the catalog, so the
    /// new tile and its settings are in view. The button's command does the adding.
    /// </summary>
    private void OnWidgetCatalogAddClick(object sender, RoutedEventArgs e) => _widgetCatalog?.Hide();

    /// <summary>Widgets page: the tile grid of the page as it is shown now (the template is instantiated on each visit).</summary>
    private void OnWidgetTilesLoaded(object sender, RoutedEventArgs e) => _widgetTiles = sender as ItemsControl;

    /// <summary>
    /// Widgets page: removing a widget takes the focused "Remove from bar" button out of the tree. Keyboard focus
    /// goes to the tile that is selected now. Queued, so the removal has been laid out first.
    /// </summary>
    private void OnWidgetTileFocusRequested(WidgetItemViewModel item) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_widgetTiles is { IsLoaded: true } tiles
            && tiles.ContainerFromItem(item) is { } container
            && FirstDescendant<Button>(container) is { } tile)
        {
            tile.Focus(FocusState.Programmatic);
        }
    });

    /// <summary>The first element of type <typeparamref name="T"/> below <paramref name="root"/>, depth first.</summary>
    private static T? FirstDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FirstDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
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
