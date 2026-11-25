using System.Globalization;
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
    private readonly WidgetHost _host;

    public ClockWidgetTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // HH:mm and MM/dd/yyyy
        _host = TestHost.Create(
            _ui,
            WidgetDescriptor.FromType(typeof(ClockWidget)),
            new WidgetInstanceSettings { InstanceId = DefaultSettings.ClockWidgetInstanceId, TypeId = DefaultSettings.ClockWidgetType },
            configure: services => services.AddSingleton<IShellCommands>(_shell).AddSingleton<TimeProvider>(_time));
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
    public void A_click_opens_the_notification_center()
    {
        _host.Activate();

        Model.Open.Execute();

        Assert.Equal(1, _shell.NotificationCenterToggles);
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
