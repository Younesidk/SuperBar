using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Tests.Widgets;

public sealed class ClockWidgetTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeShell _shell = new();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 3, 14, 9, 26, 53, 500, TimeSpan.Zero));
    private readonly FakeFlyoutSurface _surface = new();
    private readonly WidgetFlyoutCoordinator _flyouts;
    private readonly WidgetHost _host;

    public ClockWidgetTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // HH:mm and MM/dd/yyyy
        _flyouts = new WidgetFlyoutCoordinator(_surface, new ManualTime());
        _host = TestHost.Create(
            _ui,
            WidgetDescriptor.FromType(typeof(ClockWidget)),
            new WidgetInstanceSettings { InstanceId = DefaultSettings.ClockWidgetInstanceId, TypeId = DefaultSettings.ClockWidgetType },
            configure: services => services.AddSingleton<IShellCommands>(_shell).AddSingleton<TimeProvider>(_time).AddSingleton(_flyouts));
    }

    public void Dispose()
    {
        _host.Dispose();
        CultureInfo.CurrentCulture = _originalCulture;
    }

    private ClockModel Model => Assert.IsType<ClockModel>(_host.View);

    [Fact]
    public void It_shows_the_time_and_date_as_soon_as_it_is_active()
    {
        Assert.Equal("", Model.Time); // nothing is computed while the bar is hidden
        Assert.Empty(_ui.LiveTimers);

        _host.Activate();

        Assert.Equal(WidgetState.Active, _host.State);
        Assert.Equal("09:26", Model.Time);
        Assert.Equal("03/14/2026", Model.Date);
        Assert.Equal("Saturday, 14 March 2026", Model.ToolTip);
    }

    [Fact]
    public void It_wakes_once_per_minute_aligned_to_the_minute_boundary()
    {
        _host.Activate();

        // 09:26:53.500 -> 6.5 s to the next minute, plus a 50 ms safety margin.
        var timer = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromMilliseconds(6550), timer.Interval);

        _time.Now = new DateTimeOffset(2026, 3, 14, 9, 27, 0, 50, TimeSpan.Zero);
        timer.Fire();

        Assert.Equal("09:27", Model.Time);
        var next = Assert.Single(_ui.LiveTimers);
        Assert.NotSame(timer, next);
        Assert.Equal(TimeSpan.FromMilliseconds(60000), next.Interval);
    }

    [Fact]
    public void It_does_nothing_while_the_bar_is_hidden_and_catches_up_when_shown_again()
    {
        _host.Activate();

        _host.Deactivate();
        Assert.Empty(_ui.LiveTimers);

        _time.Now = new DateTimeOffset(2026, 3, 15, 18, 5, 0, TimeSpan.Zero);
        Assert.Equal("09:26", Model.Time);

        _host.Activate();
        Assert.Equal("18:05", Model.Time);
        Assert.Equal("03/15/2026", Model.Date);
        Assert.Single(_ui.LiveTimers);
    }

    [Fact]
    public void A_click_opens_the_windows_notification_center_and_calendar()
    {
        _host.Activate();

        Model.Open.Execute();

        Assert.Equal(1, _shell.NotificationCenterToggles);
        Assert.Null(_flyouts.Owner); // the clock opens Windows' own flyout, not a SuperBar one
        Assert.Empty(_surface.Calls);
    }

    [Fact]
    public void Right_click_offers_a_show_seconds_switch()
    {
        _host.Activate();

        var item = Assert.Single(_host.GetMenuItems());
        Assert.Equal("Show seconds", item.Text);
        Assert.Equal(false, item.IsChecked);

        item.Invoke(); // saves the setting and switches the clock live
        Assert.Equal("09:26:53", Model.Time);
        Assert.Equal(true, Assert.Single(_host.GetMenuItems()).IsChecked);
    }

    private static WidgetInstanceSettings Options(params (string Key, JsonNode? Value)[] options)
    {
        var settings = new WidgetInstanceSettings { InstanceId = DefaultSettings.ClockWidgetInstanceId, TypeId = DefaultSettings.ClockWidgetType };
        foreach (var (key, value) in options)
        {
            settings.Options[key] = value;
        }

        return settings;
    }

    [Fact]
    public void With_seconds_it_wakes_every_second_aligned_to_the_second()
    {
        _host.Apply(Options(("showSeconds", true)));
        _host.Activate();

        Assert.Equal("09:26:53", Model.Time);
        var timer = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromMilliseconds(520), timer.Interval);

        _time.Now = _time.Now.AddMilliseconds(520);
        timer.Fire();
        Assert.Equal("09:26:54", Model.Time);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Assert.Single(_ui.LiveTimers).Interval);
    }

    [Fact]
    public void Turning_seconds_on_and_off_live_switches_the_wake_up_rate_without_stacking_timers()
    {
        _host.Activate();
        var minuteTimer = Assert.Single(_ui.LiveTimers);

        _host.Apply(Options(("showSeconds", true)));

        Assert.Equal("09:26:53", Model.Time); // redrawn at once, not at the next minute
        var secondTimer = _ui.LiveTimers.Single(t => t != minuteTimer);
        Assert.Equal(TimeSpan.FromMilliseconds(520), secondTimer.Interval);

        // The orphaned minute wake-up fires once, does nothing and does not re-arm.
        var before = Model.Time;
        minuteTimer.Fire();
        Assert.Equal(before, Model.Time);
        Assert.Same(secondTimer, Assert.Single(_ui.LiveTimers));

        _host.Apply(Options(("showSeconds", false)));
        Assert.Equal("09:26", Model.Time);
        secondTimer.Fire(); // orphaned as well
        var timer = Assert.Single(_ui.LiveTimers);
        Assert.Equal(TimeSpan.FromMilliseconds(6550), timer.Interval);
    }

    [Fact]
    public void Settings_changed_while_hidden_apply_on_the_next_activation()
    {
        _host.Activate();
        _host.Deactivate();

        _host.Apply(Options(("showSeconds", true), ("showDate", false)));
        Assert.Empty(_ui.LiveTimers);

        _host.Activate();
        Assert.Equal("09:26:53", Model.Time);
        Assert.False(Model.ShowDate);
        Assert.Equal(TimeSpan.FromMilliseconds(520), Assert.Single(_ui.LiveTimers).Interval);
    }

    [Theory]
    [InlineData("System", false, 9, "09:26")]
    [InlineData("TwentyFourHour", false, 21, "21:26")]
    [InlineData("TwentyFourHour", true, 21, "21:26:53")]
    [InlineData("TwelveHour", false, 21, "9:26 PM")]
    [InlineData("TwelveHour", true, 9, "9:26:53 AM")]
    public void The_time_format_follows_the_settings(string format, bool seconds, int hour, string expected)
    {
        _time.Now = new DateTimeOffset(2026, 3, 14, hour, 26, 53, 500, TimeSpan.Zero);
        _host.Apply(Options(("timeFormat", format), ("showSeconds", seconds)));

        _host.Activate();

        Assert.Equal(expected, Model.Time);
    }

    [Fact]
    public void A_twelve_hour_clock_has_an_am_pm_marker_even_in_cultures_without_one()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.AMDesignator = "";
        culture.DateTimeFormat.PMDesignator = "";

        var time = ClockModel.FormatTime(new DateTime(2026, 1, 1, 15, 4, 0), culture, new ClockSettings { TimeFormat = ClockTimeFormat.TwelveHour });

        Assert.Equal("3:04 PM", time);
    }

    [Fact]
    public void The_date_can_be_hidden()
    {
        _host.Activate();
        Assert.True(Model.ShowDate);

        _host.Apply(Options(("showDate", false)));

        Assert.False(Model.ShowDate);
    }

    [Theory]
    [InlineData(0, 0, 60050)]
    [InlineData(59, 999, 51)]
    [InlineData(30, 0, 30050)]
    public void The_wait_until_the_next_minute_is_exact(int second, int millisecond, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ClockWidget.UntilNextMinute(new DateTime(2026, 1, 1, 12, 0, second, millisecond)));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeShell : IShellCommands
    {
        public int NotificationCenterToggles { get; private set; }

        public void ToggleNotificationCenter() => NotificationCenterToggles++;

        public void ToggleStartMenu()
        {
        }

        public void ToggleTrayOverflow()
        {
        }

        public void ToggleQuickSettings()
        {
        }

        public void OpenTaskManager()
        {
        }
    }
}

public class SystemResourcesWidgetTests
{
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeMetrics _metrics = new();

    private WidgetHost Host() => TestHost.Create(
        _ui,
        WidgetDescriptor.FromType(typeof(SystemResourcesWidget)),
        configure: services => services.AddSingleton<ISystemMetricsProvider>(_metrics));

    [Fact]
    public void It_samples_only_while_active()
    {
        var host = Host();
        var model = Assert.IsType<SystemResourcesModel>(host.View);

        Assert.Equal(0, _metrics.Samples);
        Assert.Equal("–", model.CpuText);

        host.Activate();

        Assert.True(_metrics.Samples > 0);
        Assert.Equal(42, model.CpuPercent);
        Assert.Equal("50%", model.MemoryText);
        Assert.Contains("8.0 / 16.0 GB", model.ToolTip);
        host.Dispose();
    }

    [Fact]
    public void Its_timer_runs_only_while_active()
    {
        var host = Host();
        Assert.Empty(_ui.Timers);

        host.Activate();
        var timer = Assert.Single(_ui.LiveTimers);
        Assert.Equal(SystemResourcesWidget.Interval, timer.Interval);

        var before = _metrics.Samples;
        timer.Fire();
        Assert.Equal(before + 1, _metrics.Samples);

        host.Deactivate();
        Assert.True(timer.IsDisposed);
        Assert.Empty(_ui.LiveTimers);

        host.Activate();
        Assert.Single(_ui.LiveTimers);

        host.Dispose();
        Assert.Empty(_ui.LiveTimers);
    }

    private sealed class FakeMetrics : ISystemMetricsProvider
    {
        public int Samples { get; private set; }

        public SystemResourceSnapshot Sample()
        {
            Samples++;
            return new SystemResourceSnapshot(42, 50, 8UL << 30, 16UL << 30);
        }
    }
}
