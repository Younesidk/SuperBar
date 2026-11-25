using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.ViewModels;
using WinRT.Interop;

namespace SuperBar.Runtime.Views;

/// <summary>
/// The one and only taskbar window. Its position and size are owned by the AppBar registration
/// (physical pixels via SetWindowPos); this class only configures the window chrome, renders and themes the surface.
/// </summary>
public sealed partial class TaskbarWindow : Window
{
    private readonly TaskbarTheme _theme;
    private readonly TaskbarAcrylicBackdrop _acrylic;

    public TaskbarWindow(TaskbarViewModel viewModel, TaskbarTheme theme, IWindowChrome chrome, ILogger<TaskbarWindow> logger)
    {
        ViewModel = viewModel;
        _theme = theme;
        _acrylic = new TaskbarAcrylicBackdrop(logger);

        // The XAML below references SB.* resources; they must exist before it loads.
        theme.RegisterResources(Application.Current.Resources);
        InitializeComponent();

        // Window-level x:Bind bindings normally initialize on Window.Activated, but the bar is shown without ever
        // being activated (it must not steal focus), so initialize them explicitly. Idempotent.
        Bindings.Initialize();

        ConfigureAppWindow();

        // Tool window (no Alt+Tab / Win+Tab), no frame, no DWM rounded corners or outline, no minimum-height clamp.
        chrome.ApplyShellSurfaceStyle(Handle);

        Root.ContextRequested += OnBarContextRequested;

        AppWindow.Closing += (_, args) => args.Cancel = !AllowClose;

        // The acrylic must stay on while the bar is inactive (which is almost always).
        Activated += (_, _) => _acrylic.KeepInputActive();
        Root.ActualThemeChanged += (_, _) => _acrylic.Update(Root.ActualTheme, _theme.SurfaceOpaque);
        Closed += (_, _) => SystemBackdrop = null; // disconnects and disposes the acrylic controller
    }

    public TaskbarViewModel ViewModel { get; }

    /// <summary>The bar refuses Alt+F4 and other close requests unless the runtime is shutting it down.</summary>
    public bool AllowClose { get; set; }

    public nint Handle => WindowNative.GetWindowHandle(this);

    /// <summary>Applies theme, colors, metrics and the backdrop. Everything updates live.</summary>
    public void ApplyAppearance(AppearanceSettings appearance)
    {
        _theme.Apply(appearance);
        Root.RequestedTheme = _theme.ElementTheme;
        _acrylic.Update(_theme.ElementTheme, _theme.SurfaceOpaque);

        // Acrylic on: blur behind the semi-transparent surface brush. Off: no backdrop, the surface brush is opaque.
        SystemBackdrop? wanted = _theme.UseAcrylic ? _acrylic : null;
        if (!ReferenceEquals(SystemBackdrop, wanted))
        {
            SystemBackdrop = wanted;
        }
    }

    // Context menus are built per request instead of being declared once in XAML: a MenuFlyout that has been shown
    // stops reacting to mouse clicks on its items (keyboard still works) once another SuperBar window, i.e. the
    // Control Center, has been active. A fresh flyout is not affected.

    private void OnBarContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        menu.Items.Add(new MenuFlyoutItem { Text = "SuperBar Control Center", Command = ViewModel.OpenControlCenterCommand });
        menu.Items.Add(new MenuFlyoutItem { Text = "Task Manager", Command = ViewModel.OpenTaskManagerCommand });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Exit SuperBar (restore Windows taskbar)", Command = ViewModel.ExitCommand });
        ShowContextMenu(menu, Root, args);
    }

    private void OnAppContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AppButtonViewModel app } button)
        {
            return;
        }

        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false, Placement = FlyoutPlacementMode.Top };
        menu.Items.Add(new MenuFlyoutItem { Text = app.DisplayName, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutItem { Text = "Open new window", Command = app.LaunchNewCommand });
        ShowContextMenu(menu, button, args);
    }

    private static void ShowContextMenu(MenuFlyout menu, FrameworkElement target, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, new FlyoutShowOptions { Position = point, Placement = menu.Placement });
        }
        else
        {
            menu.ShowAt(target);
        }

        args.Handled = true;
    }

    private void ConfigureAppWindow()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
    }
}
