using System.ComponentModel;
using System.Globalization;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;

namespace SuperBar.Widgets.BuiltIn;

/// <summary>
/// Time and date in the user's regional format. Wakes once per minute (or once per second when seconds are
/// shown), aligned to the boundary, and only while the widget is active. A click opens the Windows notification
/// center / calendar flyout; its right-click menu adds a "Show seconds" switch.
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
public sealed class ClockWidget : WidgetBase<ClockSettings, ClockModel>
{
    private readonly TimeProvider _time;
    private IWidgetScope? _scope;

    // Identifies the current wake-up chain. Changing it orphans a pending wake-up from an older schedule, which
    // then fires once, does nothing and does not re-arm: switching seconds on and off never stacks timers.
    private int _schedule;

    public ClockWidget(WidgetContext context, IShellCommands shell, TimeProvider? time = null)
        : base(context, new ClockModel())
    {
        _time = time ?? TimeProvider.System;
        On(Model.Open, shell.ToggleNotificationCenter);
        AddMenuItem("Show seconds", () => SaveSettings(s => s.ShowSeconds = !s.ShowSeconds), "\uE916", isChecked: () => Settings.ShowSeconds);
    }

    /// <summary>The wait until just after the next full minute.</summary>
    public static TimeSpan UntilNextMinute(DateTime now) =>
        TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);

    /// <summary>The wait until just after the next full second.</summary>
    public static TimeSpan UntilNextSecond(DateTime now) =>
        TimeSpan.FromMilliseconds(1000 - now.Millisecond + 20);

    protected override void OnActivated(IWidgetScope scope)
    {
        _scope = scope;
        Tick(scope, ++_schedule);
    }

    protected override void OnDeactivated() => _scope = null;

    protected override void OnSettingsChanged(ClockSettings previous)
    {
        // Redraw with the new format and restart the wake-ups at the new rate. While hidden, activation redraws.
        if (_scope is { } scope)
        {
            Tick(scope, ++_schedule);
        }
    }

    private void Tick(IWidgetScope scope, int schedule)
    {
        if (schedule != _schedule || !ReferenceEquals(scope, _scope))
        {
            return;
        }

        var now = _time.GetLocalNow().DateTime;
        Model.Update(now, CultureInfo.CurrentCulture, Settings);
        var wait = Settings.ShowSeconds ? UntilNextSecond(now) : UntilNextMinute(now);
        scope.After(wait, () => Tick(scope, schedule));
    }
}

public enum ClockTimeFormat
{
    [Description("Windows setting")]
    System,

    [Description("12-hour")]
    TwelveHour,

    [Description("24-hour")]
    TwentyFourHour,
}

/// <summary>Settings of <see cref="ClockWidget"/>; the Control Center generates its form from the attributes.</summary>
public sealed class ClockSettings
{
    [Setting("Time format", Description = "\"Windows setting\" follows your region settings.")]
    public ClockTimeFormat TimeFormat { get; set; } = ClockTimeFormat.System;

    [Setting("Show seconds", Description = "Updates every second instead of once a minute.")]
    public bool ShowSeconds { get; set; }

    [Setting("Show date")]
    public bool ShowDate { get; set; } = true;
}

/// <summary>Presentation state of <see cref="ClockWidget"/>.</summary>
public sealed class ClockModel : ObservableObject
{
    private string _time = "";
    private string _date = "";
    private string _toolTip = "";
    private bool _showDate = true;

    /// <summary>Raised by the view when the clock is clicked.</summary>
    public WidgetCommand Open { get; } = new();

    public string Time { get => _time; private set => SetProperty(ref _time, value); }

    public string Date { get => _date; private set => SetProperty(ref _date, value); }

    public bool ShowDate { get => _showDate; private set => SetProperty(ref _showDate, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    public void Update(DateTime now, CultureInfo culture, ClockSettings settings)
    {
        Time = FormatTime(now, culture, settings);
        Date = now.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        ShowDate = settings.ShowDate;
        ToolTip = now.ToString("D", culture);
    }

    public static string FormatTime(DateTime now, CultureInfo culture, ClockSettings settings)
    {
        var f = culture.DateTimeFormat;
        var seconds = settings.ShowSeconds;
        switch (settings.TimeFormat)
        {
            case ClockTimeFormat.TwentyFourHour:
                return now.ToString(seconds ? "HH:mm:ss" : "HH:mm", culture);
            case ClockTimeFormat.TwelveHour:
                var time = now.ToString(seconds ? "h:mm:ss" : "h:mm", culture);

                // Some cultures have no AM/PM designators; a 12-hour clock without them would be ambiguous.
                var designator = now.Hour < 12 ? f.AMDesignator : f.PMDesignator;
                if (string.IsNullOrEmpty(designator))
                {
                    designator = now.Hour < 12 ? "AM" : "PM";
                }

                return $"{time} {designator}";
            default:
                return now.ToString(seconds ? f.LongTimePattern : f.ShortTimePattern, culture);
        }
    }
}
