using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Tests.Widgets;

public class WidgetDescriptorTests
{
    private static WidgetDescriptor Make(double def = 100, double min = 50, double max = 200) =>
        new("test.widget", "Test", "", def, min, max, _ => new FakeWidget());

    [Theory]
    [InlineData(null, 100)]
    [InlineData(10.0, 50)]
    [InlineData(999.0, 200)]
    [InlineData(120.0, 120)]
    public void Width_is_resolved_within_the_type_limits(double? requested, double expected) =>
        Assert.Equal(expected, Make().ResolveWidth(requested));

    [Fact]
    public void Inconsistent_width_ranges_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Make(def: 300));
        Assert.Throws<ArgumentException>(() => Make(min: 0));
        Assert.Throws<ArgumentException>(() => Make(min: 150, max: 100));
    }

    [Fact]
    public void Registry_rejects_duplicate_type_ids()
    {
        Assert.Throws<InvalidOperationException>(() => new WidgetRegistry([Make(), Make()]));
    }

    [Fact]
    public void Built_in_widgets_are_registered()
    {
        var registry = new ServiceCollection().AddSuperBarWidgets().BuildServiceProvider().GetRequiredService<IWidgetRegistry>();

        Assert.True(registry.TryGet(DefaultSettings.SystemResourcesWidgetType, out var d));
        Assert.Equal(SystemResourcesWidget.Descriptor, d);
    }
}

public class WidgetHostTests
{
    private static WidgetHost Host(Func<WidgetContext, IWidget> factory, double? width = null) =>
        new(new WidgetDescriptor("test.widget", "Test", "", 100, 50, 200, factory),
            new WidgetInstanceSettings { InstanceId = "w1", TypeId = "test.widget", Width = width },
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance);

    [Fact]
    public void Widget_is_created_lazily_and_follows_its_lifecycle()
    {
        var created = 0;
        FakeWidget? widget = null;
        var host = Host(_ =>
        {
            created++;
            return widget = new FakeWidget();
        });

        Assert.Equal(WidgetState.NotCreated, host.State);
        Assert.Equal(0, created);

        host.Activate();
        Assert.Equal(1, created);
        Assert.Equal(WidgetState.Active, host.State);
        Assert.True(widget!.IsActive);

        host.Deactivate();
        Assert.Equal(WidgetState.Inactive, host.State);
        Assert.False(widget.IsActive);

        host.Dispose();
        Assert.Equal(WidgetState.Disposed, host.State);
        Assert.True(widget.IsDisposed);
        Assert.Equal(1, created);
    }

    [Fact]
    public void A_failing_widget_is_isolated_behind_a_placeholder()
    {
        var host = Host(_ => throw new InvalidOperationException("boom"));

        host.Activate();

        Assert.Equal(WidgetState.Faulted, host.State);
        var placeholder = Assert.IsType<WidgetFaultPlaceholder>(host.View);
        Assert.Equal("Widget error", placeholder.Message);
    }

    [Fact]
    public void A_widget_that_throws_on_activate_is_isolated_and_disposed()
    {
        var widget = new FakeWidget { ThrowOnActivate = true };
        var host = Host(_ => widget);

        host.Activate();

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.True(widget.IsDisposed);
        Assert.IsType<WidgetFaultPlaceholder>(host.View);
    }

    [Fact]
    public void Width_and_options_updates_are_applied_without_recreation()
    {
        FakeWidget? widget = null;
        var host = Host(_ => widget = new FakeWidget(), width: 120);
        host.Activate();

        host.Apply(new WidgetInstanceSettings { InstanceId = "w1", TypeId = "test.widget", Width = 500, Options = { ["k"] = "v" } });

        Assert.Equal(200, host.Width);
        Assert.Equal("v", widget!.Options["k"]);
    }

    [Fact]
    public void System_resources_widget_samples_only_when_active()
    {
        var metrics = new FakeMetrics();
        var services = new ServiceCollection().AddSingleton<ISystemMetricsProvider>(metrics).BuildServiceProvider();
        var widget = SystemResourcesWidget.Descriptor.Factory(new WidgetContext("r", new Dictionary<string, string>(), services, NullLogger.Instance));

        Assert.Equal(0, metrics.Samples);
        widget.Activate();
        Assert.True(metrics.Samples > 0);
        widget.Deactivate();
        widget.Dispose();
    }

    [Fact]
    public void System_resources_widget_runs_its_timer_only_while_active()
    {
        var metrics = new FakeMetrics();
        var dispatcher = new FakeDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<ISystemMetricsProvider>(metrics)
            .AddSingleton<IUiDispatcher>(dispatcher)
            .BuildServiceProvider();
        var widget = (SystemResourcesWidget)SystemResourcesWidget.Descriptor.Factory(new WidgetContext("r", new Dictionary<string, string>(), services, NullLogger.Instance));
        var timer = Assert.Single(dispatcher.Timers);

        Assert.Equal(TimeSpan.FromSeconds(2), timer.Interval);
        Assert.False(timer.IsRunning);

        widget.Activate();
        Assert.True(timer.IsRunning);
        Assert.Equal(42, widget.Model.CpuPercent);
        Assert.Equal("50%", widget.Model.MemoryText);
        Assert.Contains("8.0 / 16.0 GB", widget.Model.ToolTip);

        var before = metrics.Samples;
        timer.Fire();
        Assert.Equal(before + 1, metrics.Samples);

        widget.Deactivate();
        Assert.False(timer.IsRunning);

        widget.Activate();
        widget.Dispose();
        Assert.False(timer.IsRunning);
        Assert.True(timer.IsDisposed);
    }

    private sealed class FakeDispatcher : IUiDispatcher
    {
        public List<FakeTimer> Timers { get; } = new();

        public bool HasThreadAccess => true;

        public bool TryEnqueue(Action action)
        {
            action();
            return true;
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var timer = new FakeTimer(interval, tick);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer(TimeSpan interval, Action tick) : IUiTimer
    {
        public TimeSpan Interval { get; set; } = interval;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Fire() => tick();

        public void Dispose()
        {
            IsRunning = false;
            IsDisposed = true;
        }
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

internal sealed class FakeWidget : IWidget
{
    public object View { get; } = new object();
    public bool ThrowOnActivate { get; init; }
    public bool IsActive { get; private set; }
    public bool IsDisposed { get; private set; }
    public IReadOnlyDictionary<string, string> Options { get; private set; } = new Dictionary<string, string>();

    public void Activate() => IsActive = !ThrowOnActivate ? true : throw new InvalidOperationException("boom");

    public void Deactivate() => IsActive = false;

    public void ApplyOptions(IReadOnlyDictionary<string, string> options) => Options = options;

    public void Dispose() => IsDisposed = true;
}
