using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Shell;
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
    public ICommand OpenStartCommand { get; } = new RelayCommand(shell.OpenStartMenu);
}

public sealed class WidgetSectionViewModel(string id, WidgetHost host) : SectionViewModel(id)
{
    private double _width = host.Width;

    public WidgetHost Host { get; } = host;

    public string DisplayName => Host.Descriptor.DisplayName;

    public Control View => Host.View;

    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public void RefreshWidth() => Width = Host.Width;

    public override void Activate() => Host.Activate();

    public override void Deactivate() => Host.Deactivate();
}

/// <summary>
/// System section. Real notification-area (tray icon) hosting requires SuperBar to own the shell's
/// Shell_TrayWnd notification protocol and is NOT implemented in this milestone; the overflow button is a placeholder.
/// The quick-settings button opens the real Windows Quick Settings flyout.
/// </summary>
public sealed class SystemTraySectionViewModel(string id, IShellCommands shell) : SectionViewModel(id)
{
    public ICommand OpenQuickSettingsCommand { get; } = new RelayCommand(shell.OpenQuickSettings);

    public string OverflowToolTip => "Notification area icons are not integrated yet (planned milestone)";
}

public sealed class ClockSectionViewModel : SectionViewModel
{
    private readonly DispatcherTimer _timer;
    private string _time = "";
    private string _date = "";
    private string _toolTip = "";

    public ClockSectionViewModel(string id, IShellCommands shell) : base(id)
    {
        OpenNotificationCenterCommand = new RelayCommand(shell.OpenNotificationCenter);
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) => Update();
        Update();
    }

    public ICommand OpenNotificationCenterCommand { get; }

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
}
