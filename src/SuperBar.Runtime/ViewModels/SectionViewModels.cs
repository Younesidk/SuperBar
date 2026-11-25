using System.Globalization;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
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

public sealed class WidgetSectionViewModel(string id, WidgetHost host, WidgetViewResolver views) : SectionViewModel(id)
{
    private double _width = host.Width;
    private object? _source;
    private UIElement? _view;

    public WidgetHost Host { get; } = host;

    public string DisplayName => Host.Descriptor.DisplayName;

    /// <summary>
    /// The widget's UI element, created on first use. Follows the host: when the widget faults, the
    /// error placeholder replaces it.
    /// </summary>
    public UIElement View
    {
        get
        {
            var source = Host.View;
            if (_view is null || !ReferenceEquals(source, _source))
            {
                _source = source;
                _view = views.Resolve(source);
            }

            return _view;
        }
    }

    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public void RefreshWidth() => Width = Host.Width;

    public override void Activate()
    {
        Host.Activate();
        RefreshView();
    }

    public override void Deactivate()
    {
        Host.Deactivate();
        RefreshView();
    }

    private void RefreshView()
    {
        if (_source is not null && !ReferenceEquals(Host.View, _source))
        {
            OnPropertyChanged(nameof(View));
        }
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

public sealed class ClockSectionViewModel : SectionViewModel, IDisposable
{
    private readonly IUiTimer _timer;
    private string _time = "";
    private string _date = "";
    private string _toolTip = "";

    public ClockSectionViewModel(string id, IShellCommands shell, IUiDispatcher dispatcher) : base(id)
    {
        ToggleNotificationCenterCommand = new RelayCommand(shell.ToggleNotificationCenter);
        _timer = dispatcher.CreateTimer(TimeSpan.FromMinutes(1), Update);
        Update();
    }

    public ICommand ToggleNotificationCenterCommand { get; }

    public string Time
    {
        get => _time;
        private set => SetProperty(ref _time, value);
    }

    public string Date
    {
        get => _date;
        private set => SetProperty(ref _date, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    public override void Activate()
    {
        Update();
        _timer.Start();
    }

    public override void Deactivate() => _timer.Stop();

    private void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        Time = now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        Date = now.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        ToolTip = now.ToString("D", culture);

        // Wake exactly once per minute, aligned to the minute boundary.
        _timer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
    }

    public void Dispose() => _timer.Dispose();
}
