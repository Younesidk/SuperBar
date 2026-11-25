using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Shell;
using SuperBar.Runtime.Theming;
using SuperBar.Runtime.Widgets;
using SuperBar.Widgets;

namespace SuperBar.Runtime.ViewModels;

/// <summary>A modular section of the single taskbar surface.</summary>
public abstract class SectionViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>Called when the bar becomes visible / un-suspended.</summary>
    public virtual void Activate()
    {
    }

    /// <summary>Called when the bar is hidden or a full-screen app suspends it; stop timers here.</summary>
    public virtual void Deactivate()
    {
    }
}

public sealed class StartSectionViewModel(string id, IShellCommands shell) : SectionViewModel(id)
{
    public ICommand ToggleStartCommand { get; } = new RelayCommand(shell.ToggleStartMenu);
}

/// <summary>
/// The taskbar slot of one hosted widget. It follows its <see cref="WidgetHost"/>: whenever the host's state
/// changes what the slot shows (first view, fault placeholder, restarted widget), the previous view is released
/// and <see cref="View"/> is re-evaluated.
/// </summary>
public sealed class WidgetSectionViewModel : SectionViewModel, IDisposable
{
    private readonly WidgetViewResolver _views;
    private readonly TaskbarTheme _theme;
    private double _width;
    private object? _source;
    private UIElement? _view;
    private bool _disposed;

    public WidgetSectionViewModel(string id, WidgetHost host, WidgetViewResolver views, TaskbarTheme theme)
        : base(id)
    {
        Host = host;
        _views = views;
        _theme = theme;
        _width = SlotWidth();
        host.StateChanged += OnHostStateChanged;
    }

    public WidgetHost Host { get; }

    public string DisplayName => Host.Descriptor.DisplayName;

    /// <summary>
    /// The widget's UI element, created on first use. Follows the host: when the widget faults, the
    /// error placeholder replaces it; when it restarts, a fresh view does.
    /// </summary>
    public UIElement View
    {
        get
        {
            var source = Host.View;
            if (_view is null || !ReferenceEquals(source, _source))
            {
                ReleaseView();
                _source = source;
                _view = _views.Resolve(source);
            }

            return _view;
        }
    }

    /// <summary>The slot width in DIPs, or NaN when the widget sizes itself to its content.</summary>
    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public double MinWidth => IsAutoSized ? Host.Descriptor.MinWidth : 0;

    public double MaxWidth => IsAutoSized ? Host.Descriptor.MaxWidth : double.PositiveInfinity;

    /// <summary>The shared widget card, or nothing for widgets that draw their own chrome.</summary>
    public Brush Background => Host.Descriptor.ShowBackground ? _theme.WidgetBackground : _theme.Transparent;

    private bool IsAutoSized => Host.Descriptor.Sizing == WidgetSizing.Auto;

    public void RefreshWidth() => Width = SlotWidth();

    public override void Activate() => Host.Activate();

    public override void Deactivate() => Host.Deactivate();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Host.StateChanged -= OnHostStateChanged;
        Host.Dispose();
        ReleaseView();
    }

    private double SlotWidth() => IsAutoSized ? double.NaN : Host.Width;

    private void OnHostStateChanged(object? sender, EventArgs e)
    {
        // Only once a view has been handed out: before that the slot's first binding picks up the current one.
        if (!_disposed && _source is not null && !ReferenceEquals(Host.View, _source))
        {
            OnPropertyChanged(nameof(View));
        }
    }

    private void ReleaseView()
    {
        // A view created for a model is ours to dispose; a UI element supplied by the widget itself is not.
        if (_view is IDisposable disposable && !ReferenceEquals(_view, _source))
        {
            disposable.Dispose();
        }

        _view = null;
        _source = null;
    }
}

/// <summary>
/// System section. Real notification-area (tray icon) hosting requires SuperBar to own the shell's
/// Shell_TrayWnd notification protocol and is NOT implemented in this milestone; until then the overflow button
/// opens Explorer's own "hidden icons" flyout. The quick-settings button opens the real Windows Quick Settings flyout.
/// </summary>
public sealed class SystemTraySectionViewModel(string id, IShellCommands shell) : SectionViewModel(id)
{
    public ICommand ToggleQuickSettingsCommand { get; } = new RelayCommand(shell.ToggleQuickSettings);

    public ICommand ToggleOverflowCommand { get; } = new RelayCommand(shell.ToggleTrayOverflow);
}
