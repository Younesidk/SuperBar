using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Tests.Widgets;

/// <summary>Connected devices the test sets; counts the queries and can fail them.</summary>
internal sealed class FakeBluetoothBatteryService : IBluetoothBatteryService
{
    private volatile IReadOnlyList<BluetoothBatteryDevice> _devices = [];
    private int _calls;

    public IReadOnlyList<BluetoothBatteryDevice> Devices { get => _devices; set => _devices = value; }

    public int Calls => Volatile.Read(ref _calls);

    public volatile bool Fail;

    public Task<IReadOnlyList<BluetoothBatteryDevice>> GetConnectedDevicesAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Fail ? throw new InvalidOperationException("radio gone") : Task.FromResult(_devices);
    }
}

public sealed class BluetoothBatteryWidgetTests : IDisposable
{
    private const string Id = "bt-1";

    private static readonly BluetoothBatteryDevice Buds = new("c1", "Galaxy Buds2", 85, BluetoothDeviceKind.Earbuds);
    private static readonly BluetoothBatteryDevice Mouse = new("c2", "MX Master 3", 40, BluetoothDeviceKind.Mouse);
    private static readonly BluetoothBatteryDevice Pad = new("c3", "Xbox Wireless Controller", 62, BluetoothDeviceKind.Gamepad);

    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeBluetoothBatteryService _service = new();
    private readonly FakeFlyoutSurface _surface = new();
    private readonly WidgetFlyoutCoordinator _flyouts;
    private WidgetInstanceSettings _settings = new() { InstanceId = Id, TypeId = "superbar.bluetooth-battery" };
    private readonly WidgetHost _host;

    public BluetoothBatteryWidgetTests()
    {
        _flyouts = new WidgetFlyoutCoordinator(_surface, new ManualTime());
        _host = TestHost.Create(
            _ui,
            WidgetDescriptor.FromType(typeof(BluetoothBatteryWidget)),
            _settings,
            configure: s => s.AddSingleton(_flyouts).AddSingleton<IBluetoothBatteryService>(_service));
    }

    public void Dispose() => _host.Dispose();

    private BluetoothBatteryModel Model => Assert.IsType<BluetoothBatteryModel>(_host.View);

    /// <summary>Activates and waits for the first poll's result.</summary>
    private void Start(params BluetoothBatteryDevice[] devices)
    {
        _service.Devices = devices;
        _host.Activate();
        _ui.PumpUntil(() => _service.Calls >= 1);
        _ui.PumpFor(30);
    }

    private void Set(string key, JsonNode value)
    {
        var options = (JsonObject)_settings.Options.DeepClone();
        options[key] = value;
        _settings = new WidgetInstanceSettings { InstanceId = Id, TypeId = _settings.TypeId, Options = options };
        _host.Apply(_settings);
    }

    /// <summary>Asks for new levels right away through the widget's "Refresh" menu item and waits for them.</summary>
    private void RefreshNow()
    {
        var calls = _service.Calls;
        _host.GetMenuItems().Single(m => m.Text == "Refresh").Invoke();
        _ui.PumpUntil(() => _service.Calls > calls);
        _ui.PumpFor(30);
    }

    private BluetoothBatteryFlyoutModel OpenFlyout()
    {
        Model.Open.Execute();
        return Assert.IsType<BluetoothBatteryFlyoutModel>(_surface.Shown!.Value.Model);
    }

    [Fact]
    public void It_is_discovered_as_an_auto_sized_single_instance_widget_with_its_settings()
    {
        var registry = new ServiceCollection().AddSuperBarWidgets().BuildServiceProvider().GetRequiredService<IWidgetRegistry>();

        Assert.True(registry.TryGet("superbar.bluetooth-battery", out var d));
        Assert.Equal("Bluetooth Battery", d.DisplayName);
        Assert.Equal(WidgetSizing.Auto, d.Sizing);
        Assert.False(d.AllowMultiple);
        Assert.Equal(["hideWhenEmpty", "lowBatteryThreshold", "refreshSeconds", "compact"], d.SettingsSchema!.Fields.Select(f => f.Key));
        var threshold = d.SettingsSchema.Fields[1];
        Assert.Equal((5.0, 40.0, "%"), (threshold.Minimum!.Value, threshold.Maximum!.Value, threshold.Unit));
        var refresh = d.SettingsSchema.Fields[2];
        Assert.Equal((10.0, 300.0, "s", (object?)30), (refresh.Minimum!.Value, refresh.Maximum!.Value, refresh.Unit, refresh.DefaultValue));
    }

    [Fact]
    public void Without_devices_it_collapses_by_default()
    {
        Start();

        Assert.True(Model.IsCollapsed);
        Assert.False(Model.HasDevices);
        Assert.Empty(Model.Items);
        Assert.Equal(WidgetState.Active, _host.State);
    }

    [Fact]
    public void Without_devices_and_HideWhenEmpty_off_it_stays_visible()
    {
        Start();

        Set("hideWhenEmpty", false);

        Assert.False(Model.IsCollapsed);
        Assert.False(Model.HasDevices);
    }

    [Fact]
    public void One_device_is_shown_inline()
    {
        Start(Buds);

        var item = Assert.Single(Model.Items);
        Assert.Equal(("Galaxy Buds2", "85%", BluetoothDeviceKind.Earbuds, BatteryLevel.Normal), (item.Name, item.PercentText, item.Kind, item.Level));
        Assert.Equal("", item.Glyph);
        Assert.Equal(0, Model.HiddenCount);
        Assert.Equal("", Model.HiddenText);
        Assert.False(Model.IsCollapsed);
    }

    [Fact]
    public void Two_devices_are_both_shown_lowest_first()
    {
        Start(Buds, Mouse);

        Assert.Equal(["MX Master 3", "Galaxy Buds2"], Model.Items.Select(i => i.Name));
        Assert.Equal(0, Model.HiddenCount);
        Assert.Equal("MX Master 3: 40%\nGalaxy Buds2: 85%", Model.ToolTip);
    }

    [Fact]
    public void Three_devices_show_the_lowest_and_a_count_while_the_flyout_lists_all()
    {
        Start(Buds, Mouse, Pad);

        Assert.Equal("MX Master 3", Assert.Single(Model.Items).Name);
        Assert.Equal(2, Model.HiddenCount);
        Assert.Equal("+2", Model.HiddenText);

        var flyout = OpenFlyout();
        _ui.PumpFor(30);
        Assert.Equal(["MX Master 3", "Xbox Wireless Controller", "Galaxy Buds2"], flyout.Devices.Select(d => d.Name));
    }

    [Fact]
    public void Compact_mode_shows_only_the_lowest_device_and_can_be_toggled_from_the_menu()
    {
        Start(Buds, Mouse);

        var compact = _host.GetMenuItems().Single(m => m.Text == "Compact");
        Assert.False(compact.IsChecked);
        compact.Invoke();

        Assert.Equal("MX Master 3", Assert.Single(Model.Items).Name);
        Assert.Equal("+1", Model.HiddenText);
        Assert.True(_host.GetMenuItems().Single(m => m.Text == "Compact").IsChecked);
    }

    [Fact]
    public void Levels_turn_low_then_critical_and_recover()
    {
        Start(Mouse with { BatteryPercent = 31 });
        Assert.Equal(BatteryLevel.Normal, Model.Items[0].Level);

        _service.Devices = [Mouse with { BatteryPercent = 30 }];
        RefreshNow();
        Assert.Equal(BatteryLevel.Low, Model.Items[0].Level);

        _service.Devices = [Mouse with { BatteryPercent = 15 }];
        RefreshNow();
        Assert.Equal(BatteryLevel.Critical, Model.Items[0].Level);

        _service.Devices = [Mouse with { BatteryPercent = 80 }];
        RefreshNow();
        Assert.Equal(BatteryLevel.Normal, Model.Items[0].Level);
        Assert.Equal("80%", Model.Items[0].PercentText);
    }

    [Fact]
    public void Changing_the_threshold_recolors_without_a_new_query()
    {
        Start(Mouse); // 40%
        var calls = _service.Calls;

        Set("lowBatteryThreshold", 40);

        Assert.Equal(BatteryLevel.Low, Model.Items[0].Level);
        Set("lowBatteryThreshold", 10);
        Assert.Equal(BatteryLevel.Normal, Model.Items[0].Level);
        Assert.Equal(calls, _service.Calls);
    }

    [Theory]
    [InlineData(30, 31, BatteryLevel.Normal)]
    [InlineData(30, 30, BatteryLevel.Low)]
    [InlineData(30, 16, BatteryLevel.Low)]
    [InlineData(30, 15, BatteryLevel.Critical)]
    [InlineData(30, 0, BatteryLevel.Critical)]
    [InlineData(5, 3, BatteryLevel.Low)]
    [InlineData(5, 2, BatteryLevel.Critical)]
    public void A_level_is_low_at_the_threshold_and_critical_at_half_of_it(int threshold, int percent, BatteryLevel expected) =>
        Assert.Equal(expected, BluetoothBatteryItem.LevelOf(percent, threshold));

    [Fact]
    public void The_last_device_disconnecting_collapses_the_widget_and_a_new_one_brings_it_back()
    {
        Start(Buds);
        Assert.False(Model.IsCollapsed);

        _service.Devices = [];
        RefreshNow();
        Assert.True(Model.IsCollapsed);

        _service.Devices = [Pad];
        RefreshNow();
        Assert.False(Model.IsCollapsed);
        Assert.Equal("Xbox Wireless Controller", Assert.Single(Model.Items).Name);
    }

    [Fact]
    public void Values_outside_0_to_100_are_ignored()
    {
        Start(Buds with { BatteryPercent = 120 }, Mouse with { BatteryPercent = -1 }, Pad);

        Assert.Equal("Xbox Wireless Controller", Assert.Single(Model.Items).Name);
        Assert.Equal(0, Model.HiddenCount);
    }

    [Fact]
    public void Unchanged_polls_keep_the_same_items()
    {
        Start(Buds, Mouse);
        var items = Model.Items;
        var changes = new List<string?>();
        Model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        RefreshNow();

        Assert.Same(items, Model.Items);
        Assert.DoesNotContain(nameof(BluetoothBatteryModel.Items), changes);
    }

    [Fact]
    public void A_failing_query_keeps_the_last_levels_and_does_not_fault_the_widget()
    {
        Start(Buds);
        _service.Fail = true;

        var calls = _service.Calls;
        _host.GetMenuItems().Single(m => m.Text == "Refresh").Invoke();
        _ui.PumpUntil(() => _service.Calls > calls);
        _ui.PumpFor(30);

        Assert.Equal(WidgetState.Active, _host.State);
        Assert.Equal("Galaxy Buds2", Assert.Single(Model.Items).Name);

        // The next refresh works again (the failed one did not leave the widget stuck "refreshing").
        _service.Fail = false;
        _service.Devices = [Pad];
        RefreshNow();
        Assert.Equal("Xbox Wireless Controller", Assert.Single(Model.Items).Name);
    }

    [Fact]
    public void Changing_the_refresh_interval_restarts_polling_at_once()
    {
        Start(Buds);
        Assert.Equal(1, _service.Calls);
        _ui.PumpFor(100);
        Assert.Equal(1, _service.Calls); // the next run is 30 s away

        Set("refreshSeconds", 60);

        _ui.PumpUntil(() => _service.Calls == 2); // the new loop runs right away
        _ui.PumpFor(100);
        Assert.Equal(2, _service.Calls);
    }

    [Fact]
    public void Other_setting_changes_do_not_restart_polling()
    {
        Start(Buds);

        Set("hideWhenEmpty", false);
        Set("compact", true);
        _ui.PumpFor(100);

        Assert.Equal(1, _service.Calls);
    }

    [Fact]
    public void Clicking_toggles_the_flyout_and_opening_it_refreshes_the_levels()
    {
        Start(Buds);
        _service.Devices = [Buds with { BatteryPercent = 70 }];

        var flyout = OpenFlyout();

        Assert.True(_flyouts.IsOpenFor(Id));
        Assert.True(flyout.IsRefreshing);
        Assert.False(flyout.Refresh.CanExecute());
        _ui.PumpUntil(() => !flyout.IsRefreshing);
        Assert.Equal(2, _service.Calls);
        Assert.Equal("70%", Assert.Single(flyout.Devices).PercentText);
        Assert.Equal("70%", Assert.Single(Model.Items).PercentText);
        Assert.True(flyout.Refresh.CanExecute());

        Model.Open.Execute();

        Assert.False(_flyouts.IsOpenFor(Id));
        Assert.Equal("hide", _surface.Calls[^1]);
        _ui.PumpFor(50);
        Assert.Equal(2, _service.Calls); // closing does not query
    }

    [Fact]
    public void The_flyouts_refresh_button_queries_again()
    {
        Start(Buds);
        var flyout = OpenFlyout();
        _ui.PumpUntil(() => !flyout.IsRefreshing);
        _service.Devices = [Buds, Pad];

        flyout.Refresh.Execute();
        _ui.PumpUntil(() => !flyout.IsRefreshing);

        Assert.Equal(3, _service.Calls);
        Assert.Equal(["Xbox Wireless Controller", "Galaxy Buds2"], flyout.Devices.Select(d => d.Name));
    }

    [Fact]
    public void Deactivation_stops_polling_closes_the_flyout_and_drops_late_results()
    {
        Start(Buds);
        var flyout = OpenFlyout();
        _ui.PumpUntil(() => !flyout.IsRefreshing);
        var calls = _service.Calls;

        _host.Deactivate();
        _service.Devices = [Pad];
        _host.GetMenuItems().Single(m => m.Text == "Refresh").Invoke(); // ignored while inactive
        _ui.PumpFor(100);

        Assert.False(_flyouts.IsOpenFor(Id));
        Assert.Equal(calls, _service.Calls);
        Assert.Equal("Galaxy Buds2", Assert.Single(Model.Items).Name);

        // Activating again starts a fresh poll.
        _host.Activate();
        _ui.PumpUntil(() => _service.Calls > calls);
        _ui.PumpFor(30);
        Assert.Equal("Xbox Wireless Controller", Assert.Single(Model.Items).Name);
    }

    [Fact]
    public void Disposing_unbinds_the_commands()
    {
        Start(Buds);
        var open = Model.Open;

        _host.Dispose();

        Assert.False(open.CanExecute());
        Assert.Equal(WidgetState.Disposed, _host.State);
    }
}
