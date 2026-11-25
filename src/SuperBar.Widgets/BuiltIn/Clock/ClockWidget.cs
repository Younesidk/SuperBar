using System.Globalization;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Time and date in the user's regional format. Wakes once per minute, aligned to the minute boundary, and only
/// while the widget is active. A click opens the Windows notification center / calendar flyout.
/// </summary>
[Widget(DefaultSettings.ClockWidgetType, "Clock",
    Description = "Time and date. Click to open the notification center.",
    Glyph = "\uE823",
    Sizing = WidgetSizing.Auto,
    ShowBackground = false,
    DefaultWidth = 96,
    MinWidth = 48,
    MaxWidth = 320,
    AllowMultiple = false)]
public sealed class ClockWidget : WidgetBase
{
    private readonly TimeProvider _time;

    public ClockWidget(WidgetContext context, IShellCommands shell, TimeProvider? time = null)
        : base(context)
    {
        _time = time ?? TimeProvider.System;
        On(Model.Open, shell.ToggleNotificationCenter);
    }

    public ClockModel Model { get; } = new();

    public override object ViewModel => Model;

    /// <summary>The wait until just after the next full minute.</summary>
    public static TimeSpan UntilNextMinute(DateTime now) =>
        TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);

    protected override void OnActivated(IWidgetScope scope) => Tick(scope);

    private void Tick(IWidgetScope scope)
    {
        var now = _time.GetLocalNow().DateTime;
        Model.Update(now, CultureInfo.CurrentCulture);
        scope.After(UntilNextMinute(now), () => Tick(scope));
    }
}

/// <summary>Presentation state of <see cref="ClockWidget"/>.</summary>
public sealed class ClockModel : ObservableObject
{
    private string _time = "";
    private string _date = "";
    private string _toolTip = "";

    /// <summary>Raised by the view when the clock is clicked.</summary>
    public WidgetCommand Open { get; } = new();

    public string Time { get => _time; private set => SetProperty(ref _time, value); }

    public string Date { get => _date; private set => SetProperty(ref _date, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    public void Update(DateTime now, CultureInfo culture)
    {
        Time = now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        Date = now.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        ToolTip = now.ToString("D", culture);
    }
}
