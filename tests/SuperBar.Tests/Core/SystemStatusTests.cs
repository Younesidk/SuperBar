using SuperBar.Core.Shell;

namespace SuperBar.Tests.Core;

/// <summary>Which connection the tray shows, and the glyphs and tooltips of its network and battery icons.</summary>
public class SystemStatusTests
{
    private static NetworkCandidate Candidate(
        NetworkKind kind,
        NetworkReach reach,
        string name = "net",
        bool isVirtual = false,
        bool internet = false,
        int? bars = null) =>
        new(kind, reach, name, bars, isVirtual, internet);

    [Fact]
    public void No_connected_profiles_means_disconnected()
    {
        Assert.Equal(NetworkState.Disconnected, NetworkStateResolver.Resolve([]));
        Assert.Equal(NetworkState.Disconnected, NetworkStateResolver.Resolve([Candidate(NetworkKind.WiFi, NetworkReach.Disconnected)]));
    }

    [Fact]
    public void Ethernet_is_shown_when_it_carries_internet()
    {
        var state = NetworkStateResolver.Resolve([Candidate(NetworkKind.Ethernet, NetworkReach.Internet, "Network 3", internet: true)]);

        Assert.Equal(new NetworkState(NetworkKind.Ethernet, NetworkReach.Internet, "Network 3"), state);
    }

    [Fact]
    public void With_ethernet_and_wifi_up_the_internet_profile_wins()
    {
        var state = NetworkStateResolver.Resolve(
        [
            Candidate(NetworkKind.Ethernet, NetworkReach.Internet, "Wired"),
            Candidate(NetworkKind.WiFi, NetworkReach.Internet, "Home", internet: true, bars: 4),
        ]);

        Assert.Equal(NetworkKind.WiFi, state.Kind);
        Assert.Equal("Home", state.Name);
        Assert.Equal(4, state.SignalBars);
    }

    [Fact]
    public void Virtual_adapters_do_not_count_as_a_local_connection()
    {
        // A desktop with WSL / Hyper-V and the cable pulled: the vEthernet switch is "connected" locally.
        var state = NetworkStateResolver.Resolve([Candidate(NetworkKind.Ethernet, NetworkReach.LocalOnly, "vEthernet (WSL)", isVirtual: true)]);

        Assert.Equal(NetworkState.Disconnected, state);
    }

    [Fact]
    public void A_physical_link_without_internet_is_local_only()
    {
        var state = NetworkStateResolver.Resolve(
        [
            Candidate(NetworkKind.Ethernet, NetworkReach.LocalOnly, "vEthernet (Default Switch)", isVirtual: true),
            Candidate(NetworkKind.WiFi, NetworkReach.LocalOnly, "Cafe", bars: 2),
        ]);

        Assert.Equal(NetworkKind.WiFi, state.Kind);
        Assert.Equal(NetworkReach.LocalOnly, state.Reach);
    }

    [Fact]
    public void A_vpn_default_route_shows_the_physical_link_beneath_it()
    {
        var state = NetworkStateResolver.Resolve(
        [
            Candidate(NetworkKind.Other, NetworkReach.Internet, "Tailscale", isVirtual: true, internet: true),
            Candidate(NetworkKind.WiFi, NetworkReach.Internet, "Home", bars: 5),
        ]);

        Assert.Equal(NetworkKind.WiFi, state.Kind);
        Assert.Equal(NetworkReach.Internet, state.Reach);
        Assert.Equal("Tailscale", state.RoutedThrough);
    }

    [Fact]
    public void A_virtual_default_route_with_no_physical_link_is_shown_as_it_is()
    {
        // Hyper-V external switch: the host's only route is the vEthernet adapter bound to the NIC.
        var state = NetworkStateResolver.Resolve([Candidate(NetworkKind.Ethernet, NetworkReach.Internet, "Network", isVirtual: true, internet: true)]);

        Assert.Equal(new NetworkState(NetworkKind.Ethernet, NetworkReach.Internet, "Network"), state);
    }

    [Fact]
    public void Other_links_such_as_tethering_get_a_fallback_icon_not_nothing()
    {
        var visual = TrayIndicators.For(new NetworkState(NetworkKind.Other, NetworkReach.Internet, "Phone"));

        Assert.Equal("\uE774", visual.Glyph);
        Assert.False(visual.IsDimmed);
        Assert.Contains("Internet access", visual.ToolTip);
    }

    [Fact]
    public void Ethernet_without_internet_shows_the_warning_glyph_and_says_so()
    {
        var online = TrayIndicators.For(new NetworkState(NetworkKind.Ethernet, NetworkReach.Internet, "Network 3"));
        var local = TrayIndicators.For(new NetworkState(NetworkKind.Ethernet, NetworkReach.LocalOnly, "Network 3"));

        Assert.Equal("\uE839", online.Glyph);
        Assert.Equal("\uEB56", local.Glyph);
        Assert.Equal("Network 3\nEthernet · No internet access", local.ToolTip);
    }

    [Fact]
    public void Disconnected_and_local_only_look_different()
    {
        var offline = TrayIndicators.For(NetworkState.Disconnected);
        var local = TrayIndicators.For(new NetworkState(NetworkKind.WiFi, NetworkReach.LocalOnly, "Cafe", 4));

        Assert.NotEqual(offline.Glyph, local.Glyph);
        Assert.StartsWith("Not connected", offline.ToolTip);
    }

    [Theory]
    [InlineData(1, "\uE872")]
    [InlineData(2, "\uE873")]
    [InlineData(3, "\uE874")]
    [InlineData(5, "\uE701")]
    public void Wifi_strength_picks_the_bars_glyph(int bars, string glyph)
    {
        var visual = TrayIndicators.For(new NetworkState(NetworkKind.WiFi, NetworkReach.Internet, "Home", bars));

        Assert.Equal(glyph, visual.Glyph);
        Assert.Equal(glyph == "\uE701" ? "" : "\uE701", visual.Ghost);
    }

    [Fact]
    public void Routed_traffic_is_named_in_the_tooltip()
    {
        var visual = TrayIndicators.For(new NetworkState(NetworkKind.WiFi, NetworkReach.Internet, "Home", 5, "Tailscale"));

        Assert.EndsWith("Routed through Tailscale", visual.ToolTip);
    }

    [Theory]
    [InlineData(40, true, true, false, BatteryFill.PluggedIn)]
    [InlineData(5, true, true, true, BatteryFill.PluggedIn)]
    [InlineData(100, false, true, false, BatteryFill.PluggedIn)]
    [InlineData(80, false, true, false, BatteryFill.PluggedIn)]
    [InlineData(10, false, false, true, BatteryFill.Critical)]
    [InlineData(10, false, false, false, BatteryFill.Critical)]
    [InlineData(11, false, false, true, BatteryFill.EnergySaver)]
    [InlineData(11, false, false, false, BatteryFill.Normal)]
    [InlineData(90, false, false, false, BatteryFill.Normal)]
    public void Battery_fill_follows_the_windows_priority(int percent, bool charging, bool pluggedIn, bool saver, BatteryFill fill)
    {
        // AC power first, then critical (even over energy saver), then energy saver, then normal.
        Assert.Equal(fill, TrayIndicators.FillFor(percent, charging, pluggedIn, saver));
    }

    [Fact]
    public void Battery_tooltips_give_percentage_status_time_and_energy_saver()
    {
        var discharging = TrayIndicators.BatteryToolTip(new BatteryState(true, 82, TimeRemaining: TimeSpan.FromMinutes(190), IsSaverOn: true));
        var charging = TrayIndicators.BatteryToolTip(new BatteryState(true, 64, IsCharging: true, IsPluggedIn: true, TimeToFull: TimeSpan.FromMinutes(45)));
        var full = TrayIndicators.BatteryToolTip(new BatteryState(true, 100, IsPluggedIn: true));
        var held = TrayIndicators.BatteryToolTip(new BatteryState(true, 80, IsPluggedIn: true));

        Assert.Equal("Battery · 82%\n3 hr 10 min remaining\nEnergy saver is on", discharging);
        Assert.Equal("Charging · 64%\n45 min until fully charged", charging);
        Assert.Equal("Plugged in · fully charged (100%)", full);
        Assert.Equal("Plugged in, not charging · 80%", held);
    }

    [Theory]
    [InlineData(0.4, "Less than 1 min")]
    [InlineData(60, "1 hr")]
    [InlineData(125, "2 hr 5 min")]
    public void Durations_read_like_windows(double minutes, string text)
    {
        Assert.Equal(text, TrayIndicators.FormatDuration(TimeSpan.FromMinutes(minutes)));
    }
}
