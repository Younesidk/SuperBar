using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Layout;
using SuperBar.Core.Shell;
using SuperBar.Widgets;

namespace SuperBar.Tests.Widgets;

/// <summary>A flyout surface that records what the coordinator asks of it, and lets a test dismiss it like a user would.</summary>
internal sealed class FakeFlyoutSurface : IWidgetFlyoutSurface
{
    public List<string> Calls { get; } = new();
    public (string Owner, object Model)? Shown { get; private set; }
    public bool Refuse { get; set; }
    public bool Throw { get; set; }

    public event EventHandler? Dismissed;

    public bool Show(string ownerInstanceId, object model)
    {
        if (Throw)
        {
            throw new InvalidOperationException("view failed");
        }

        Calls.Add("show " + ownerInstanceId);
        if (Refuse)
        {
            return false;
        }

        Shown = (ownerInstanceId, model);
        return true;
    }

    public void Hide()
    {
        Calls.Add("hide");
        Shown = null;
    }

    /// <summary>A click outside, Escape, or another window taking the foreground.</summary>
    public void LightDismiss()
    {
        Shown = null;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

public class WidgetFlyoutCoordinatorTests
{
    private readonly FakeFlyoutSurface _surface = new();
    private readonly ManualTime _time = new();
    private readonly WidgetFlyoutCoordinator _flyouts;
    private readonly object _calendar = new();
    private readonly object _other = new();

    public WidgetFlyoutCoordinatorTests() => _flyouts = new WidgetFlyoutCoordinator(_surface, _time);

    [Fact]
    public void Toggle_opens_and_a_second_toggle_closes()
    {
        _flyouts.Toggle("clock", _calendar);
        Assert.True(_flyouts.IsOpenFor("clock"));
        Assert.Equal(("clock", _calendar), _surface.Shown);

        _flyouts.Toggle("clock", _calendar);
        Assert.False(_flyouts.IsOpenFor("clock"));
        Assert.Null(_surface.Shown);
        Assert.Equal(["show clock", "hide"], _surface.Calls);
    }

    [Fact]
    public void Only_one_flyout_is_open_on_the_bar()
    {
        _flyouts.Open("clock", _calendar);
        _flyouts.Open("weather", _other);

        Assert.Equal("weather", _flyouts.Owner);
        Assert.False(_flyouts.IsOpenFor("clock"));
        Assert.Equal(("weather", _other), _surface.Shown);

        // The old owner closing its flyout must not close the new one.
        _flyouts.Close("clock");
        Assert.Equal("weather", _flyouts.Owner);
    }

    [Fact]
    public void Toggling_a_different_model_of_the_same_widget_switches_content()
    {
        _flyouts.Toggle("clock", _calendar);
        _flyouts.Toggle("clock", _other);

        Assert.Same(_other, _flyouts.Model);
        Assert.Equal(["show clock", "show clock"], _surface.Calls);
    }

    [Fact]
    public void The_click_that_light_dismissed_a_flyout_does_not_reopen_it()
    {
        _flyouts.Toggle("clock", _calendar);

        // Pointer down on the clock: the flyout light-dismisses. Then the click reaches the clock, which toggles.
        _surface.LightDismiss();
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _flyouts.Toggle("clock", _calendar);

        Assert.False(_flyouts.IsOpenFor("clock"));
        Assert.Equal(["show clock"], _surface.Calls);

        // The next, separate click opens it again.
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _flyouts.Toggle("clock", _calendar);
        Assert.True(_flyouts.IsOpenFor("clock"));
    }

    [Fact]
    public void A_toggle_long_after_a_dismiss_opens_normally()
    {
        _flyouts.Toggle("clock", _calendar);
        _surface.LightDismiss();
        _time.Advance(WidgetFlyoutCoordinator.ToggleGuard + TimeSpan.FromMilliseconds(1));

        _flyouts.Toggle("clock", _calendar);

        Assert.True(_flyouts.IsOpenFor("clock"));
    }

    [Fact]
    public void A_dismiss_only_guards_the_same_widget_and_model()
    {
        _flyouts.Toggle("clock", _calendar);
        _surface.LightDismiss();

        // Clicking another widget right after dismissing the clock's flyout opens that widget's flyout.
        _flyouts.Toggle("weather", _other);

        Assert.True(_flyouts.IsOpenFor("weather"));
    }

    [Fact]
    public void A_flyout_the_bar_cannot_show_is_not_reported_open()
    {
        _surface.Refuse = true;
        _flyouts.Open("clock", _calendar);
        Assert.Null(_flyouts.Owner);

        _surface.Refuse = false;
        _surface.Throw = true;
        _flyouts.Open("clock", _calendar); // logged, not thrown into the widget
        Assert.Null(_flyouts.Owner);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var changes = 0;
        _flyouts.Changed += (_, _) => changes++;

        _flyouts.Open("clock", _calendar);
        _flyouts.Open("clock", _calendar); // already open: no change
        _surface.LightDismiss();
        _flyouts.CloseAll();

        Assert.Equal(2, changes);
    }
}

public class WidgetFlyoutHostingTests
{
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeFlyoutSurface _surface = new();
    private readonly WidgetFlyoutCoordinator _flyouts;

    public WidgetFlyoutHostingTests() => _flyouts = new WidgetFlyoutCoordinator(_surface, new ManualTime());

    private WidgetHost Host(string id, out Func<FlyoutWidget> widget, bool throwOnTick = false)
    {
        FlyoutWidget? created = null;
        widget = () => created!;
        var descriptor = new WidgetDescriptor("test.flyout", "Flyout", (_, context) => created = new FlyoutWidget(context) { ThrowOnTick = throwOnTick });
        var services = new ServiceCollection().AddSingleton<SuperBar.Core.Runtime.IUiDispatcher>(_ui).AddSingleton(_flyouts).BuildServiceProvider();
        return new WidgetHost(descriptor, new SuperBar.Core.Settings.WidgetInstanceSettings { InstanceId = id, TypeId = "test.flyout" }, services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    [Fact]
    public void A_widget_opens_its_flyout_through_its_context()
    {
        var host = Host("w1", out var widget);
        host.Activate();

        widget().Model.Click.Execute();

        Assert.True(_flyouts.IsOpenFor("w1"));
        Assert.Same(widget().Panel, _surface.Shown!.Value.Model);
        Assert.True(widget().FlyoutIsOpen);
    }

    [Fact]
    public void A_widget_cannot_open_a_flyout_while_it_is_not_active()
    {
        var host = Host("w1", out var widget);
        _ = host.View; // created and initialized, but the bar is hidden

        widget().Model.Click.Execute();

        Assert.Null(_flyouts.Owner);
        Assert.Empty(_surface.Calls);
    }

    [Fact]
    public void The_flyout_closes_when_the_bar_deactivates_the_widget()
    {
        var host = Host("w1", out var widget);
        host.Activate();
        widget().Model.Click.Execute();

        host.Deactivate(); // a full-screen app, or SuperBar disabled

        Assert.Null(_flyouts.Owner);
        Assert.Equal("hide", _surface.Calls[^1]);
    }

    [Fact]
    public void The_flyout_closes_when_its_widget_faults()
    {
        var host = Host("w1", out var widget, throwOnTick: true);
        host.Activate();
        widget().Model.Click.Execute();

        Assert.Single(_ui.LiveTimers).Fire();

        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.Null(_flyouts.Owner);
        Assert.Null(_surface.Shown);
    }

    [Fact]
    public void The_flyout_closes_when_its_widget_is_removed()
    {
        var host = Host("w1", out var widget);
        host.Activate();
        widget().Model.Click.Execute();

        host.Dispose();

        Assert.Null(_flyouts.Owner);
    }

    [Fact]
    public void Removing_another_widget_leaves_the_open_flyout_alone()
    {
        var a = Host("a", out var widgetA);
        var b = Host("b", out _);
        a.Activate();
        b.Activate();
        widgetA().Model.Click.Execute();

        b.Dispose();

        Assert.True(_flyouts.IsOpenFor("a"));
    }

    [Fact]
    public void Opening_one_widgets_flyout_closes_anothers()
    {
        var a = Host("a", out var widgetA);
        var b = Host("b", out var widgetB);
        a.Activate();
        b.Activate();

        widgetA().Model.Click.Execute();
        widgetB().Model.Click.Execute();

        Assert.False(widgetA().FlyoutIsOpen);
        Assert.True(widgetB().FlyoutIsOpen);
    }

    [Fact]
    public void Menu_items_carry_their_state_and_run_behind_the_fault_guard()
    {
        var host = Host("w1", out var widget);
        host.Activate();

        var items = host.GetMenuItems();
        Assert.Equal(["Pin", "Explode"], items.Select(i => i.Text));
        Assert.Equal(false, items[0].IsChecked);
        Assert.Null(items[1].IsChecked);

        items[0].Invoke();
        Assert.Equal(true, host.GetMenuItems()[0].IsChecked);

        host.GetMenuItems()[1].Invoke(); // throws inside the widget
        Assert.Equal(WidgetState.Faulted, host.State);
        Assert.Empty(host.GetMenuItems());
    }

    [Fact]
    public void A_widget_whose_menu_state_throws_faults_and_adds_no_items()
    {
        var host = Host("w1", out var widget);
        host.Activate();
        widget().BreakMenu = true;

        Assert.Empty(host.GetMenuItems());
        Assert.Equal(WidgetState.Faulted, host.State);
    }

    [Fact]
    public void A_widget_that_is_not_running_has_no_menu_items()
    {
        var host = TestHost.Create(_ui, (_, _) => throw new InvalidOperationException("boom"));
        host.Activate();

        Assert.Empty(host.GetMenuItems());
        Assert.Empty(Host("w1", out _).GetMenuItems()); // not created yet
    }

    internal sealed class FlyoutModel
    {
        public WidgetCommand Click { get; } = new();
    }

    internal sealed class FlyoutWidget : WidgetBase<NoSettings, FlyoutModel>
    {
        private bool _pinned;

        public FlyoutWidget(WidgetContext context)
            : base(context, new FlyoutModel())
        {
            On(Model.Click, () => Context.Flyout.Toggle(Panel));
            AddMenuItem("Pin", () => _pinned = !_pinned, isChecked: () => BreakMenu ? throw new InvalidOperationException("menu") : _pinned);
            AddMenuItem("Explode", () => throw new InvalidOperationException("explode"));
        }

        public object Panel { get; } = new();
        public bool ThrowOnTick { get; init; }
        public bool BreakMenu { get; set; }
        public bool FlyoutIsOpen => Context.Flyout.IsOpen;

        protected override void OnActivated(IWidgetScope scope) =>
            scope.Every(TimeSpan.FromSeconds(1), () =>
            {
                if (ThrowOnTick)
                {
                    throw new InvalidOperationException("tick");
                }
            });
    }

    internal sealed class NoSettings;
}

public class FlyoutPlacementTests
{
    [Fact]
    public void A_flyout_is_centered_on_its_anchor_when_it_fits() =>
        Assert.Equal(900, FlyoutPlacement.CenterX(anchorCenter: 900, flyoutWidth: 320, barWidth: 1920));

    [Theory]
    [InlineData(1900, 1920, 1752)] // near the right edge: 1920 - 8 - 160
    [InlineData(10, 1920, 168)]    // near the left edge: 8 + 160
    public void A_flyout_near_an_edge_is_kept_inside_the_monitor(double anchor, double barWidth, double expected) =>
        Assert.Equal(expected, FlyoutPlacement.CenterX(anchor, 320, barWidth));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Clamping_is_done_in_dips_so_it_holds_at_every_dpi(double scale)
    {
        // The same 1920 px monitor is 1920 / scale DIPs wide. A clock at its right end must still fit.
        var barWidth = 1920 / scale;
        var center = FlyoutPlacement.CenterX(anchorCenter: barWidth - 40, flyoutWidth: 340, barWidth);

        Assert.True(center + 170 <= barWidth - FlyoutPlacement.EdgeMargin + 1e-9);
        Assert.True(center - 170 >= FlyoutPlacement.EdgeMargin - 1e-9);
    }

    [Fact]
    public void A_flyout_wider_than_the_bar_is_centered_on_it() =>
        Assert.Equal(400, FlyoutPlacement.CenterX(anchorCenter: 10, flyoutWidth: 900, barWidth: 800));

    [Fact]
    public void Flyouts_open_away_from_the_bars_edge()
    {
        Assert.True(FlyoutPlacement.OpensUpward(ScreenEdge.Bottom));
        Assert.False(FlyoutPlacement.OpensUpward(ScreenEdge.Top));
    }
}
