namespace SuperBar.Core.Shell;

// ---------------------------------------------------------------------------------------------
// Network and battery state behind the system tray's status icons. Implemented by SuperBar.Windows.
// Both services follow Windows only while something is subscribed to Changed: subscribe and unsubscribe on the UI
// thread; Changed is raised on the UI thread.
// ---------------------------------------------------------------------------------------------

/// <summary>What kind of link carries the connection shown in the tray.</summary>
public enum NetworkKind
{
    /// <summary>Not read yet.</summary>
    Unknown,

    /// <summary>No connection at all.</summary>
    None,
    Ethernet,
    WiFi,
    Cellular,

    /// <summary>Anything else that connects: USB or Bluetooth tethering, a bridge, a tunnel.</summary>
    Other,
}

/// <summary>How far the connection reaches.</summary>
public enum NetworkReach
{
    Disconnected,

    /// <summary>Connected to a network that does not reach the internet.</summary>
    LocalOnly,

    /// <summary>Limited internet, typically a captive portal waiting for a sign-in.</summary>
    Constrained,
    Internet,
}

/// <param name="Name">The SSID for Wi-Fi, otherwise the connection profile's name ("Network 3"). Empty when none.</param>
/// <param name="SignalBars">0..5 for Wi-Fi and cellular, when Windows reports it.</param>
/// <param name="RoutedThrough">
/// The virtual adapter or VPN that actually carries internet traffic while <paramref name="Kind"/> shows the physical
/// link beneath it; null when traffic goes straight out.
/// </param>
public sealed record NetworkState(NetworkKind Kind, NetworkReach Reach, string Name, int? SignalBars = null, string? RoutedThrough = null)
{
    public static NetworkState Unknown { get; } = new(NetworkKind.Unknown, NetworkReach.Disconnected, "");

    public static NetworkState Disconnected { get; } = new(NetworkKind.None, NetworkReach.Disconnected, "");
}

/// <param name="IsPresent">False on desktops and on anything else without a system battery: the indicator is hidden.</param>
/// <param name="Percent">0..100.</param>
/// <param name="IsCharging">The battery is taking charge right now.</param>
/// <param name="IsPluggedIn">On AC power (charging, full, or held back by a charge limit).</param>
/// <param name="IsSaverOn">Battery saver / energy saver is on.</param>
/// <param name="TimeRemaining">Windows' estimate of the time left on battery; null while plugged in or not estimated yet.</param>
/// <param name="TimeToFull">Estimated time until fully charged; null unless charging at a known rate.</param>
public sealed record BatteryState(
    bool IsPresent,
    int Percent = 0,
    bool IsCharging = false,
    bool IsPluggedIn = false,
    bool IsSaverOn = false,
    TimeSpan? TimeRemaining = null,
    TimeSpan? TimeToFull = null)
{
    public static BatteryState NotPresent { get; } = new(false);
}

/// <summary>The connection Windows routes internet traffic through (or the best local one when there is none).</summary>
public interface INetworkStateService
{
    NetworkState Current { get; }

    event EventHandler<NetworkState>? Changed;
}

/// <summary>The system battery, as the Windows battery flyout shows it.</summary>
public interface IBatteryStateService
{
    BatteryState Current { get; }

    event EventHandler<BatteryState>? Changed;
}

/// <summary>One connection profile Windows reports, reduced to what the tray needs.</summary>
/// <param name="IsVirtual">A virtual switch, VM, container or tunnel adapter (Hyper-V, WSL, Docker, VPNs, Tailscale…).</param>
/// <param name="IsInternetProfile">The profile <c>GetInternetConnectionProfile()</c> returned: Windows' default route.</param>
public sealed record NetworkCandidate(NetworkKind Kind, NetworkReach Reach, string Name, int? SignalBars, bool IsVirtual, bool IsInternetProfile);

/// <summary>Picks what the tray shows from the connection profiles Windows reports.</summary>
public static class NetworkStateResolver
{
    /// <summary>
    /// <list type="number">
    /// <item>The internet profile (Windows' default route) wins, so with Ethernet and Wi-Fi both up the tray shows the
    /// one Windows actually uses.</item>
    /// <item>When that profile is a virtual adapter (a VPN, Tailscale, a Hyper-V switch), the physical link beneath it is
    /// shown instead, noting what traffic is routed through. Without a connected physical link the virtual one is shown
    /// as it is.</item>
    /// <item>Without an internet profile, the best connected physical link is shown as local-only or limited. Virtual
    /// adapters never count here: Hyper-V's and WSL's are "connected" on every machine that has them.</item>
    /// </list>
    /// </summary>
    public static NetworkState Resolve(IReadOnlyList<NetworkCandidate> candidates)
    {
        var connected = candidates.Where(c => c.Reach != NetworkReach.Disconnected && c.Kind is not (NetworkKind.None or NetworkKind.Unknown)).ToList();
        var physical = connected
            .Where(c => !c.IsVirtual)
            .OrderByDescending(c => c.IsInternetProfile)
            .ThenByDescending(c => c.Reach)
            .ThenBy(c => KindRank(c.Kind))
            .FirstOrDefault();

        var internet = connected.FirstOrDefault(c => c.IsInternetProfile);
        if (internet is not null && internet.IsVirtual)
        {
            if (physical is null)
            {
                return ToState(internet);
            }

            return ToState(physical) with
            {
                Reach = internet.Reach > physical.Reach ? internet.Reach : physical.Reach,
                RoutedThrough = string.IsNullOrWhiteSpace(internet.Name) ? "a virtual adapter" : internet.Name,
            };
        }

        return internet is not null ? ToState(internet) : physical is not null ? ToState(physical) : NetworkState.Disconnected;
    }

    private static NetworkState ToState(NetworkCandidate c) => new(c.Kind, c.Reach, c.Name, c.SignalBars);

    private static int KindRank(NetworkKind kind) => kind switch
    {
        NetworkKind.Ethernet => 0,
        NetworkKind.WiFi => 1,
        NetworkKind.Cellular => 2,
        _ => 3,
    };
}

/// <summary>
/// The color of the battery icon's fill, as Windows 11's colored battery icon picks it. In priority order: on AC power
/// (charging, full, or held back by a charge limit) green; critical red, even with energy saver on; energy saver orange;
/// otherwise the bar's foreground.
/// </summary>
public enum BatteryFill
{
    Normal,
    PluggedIn,
    Critical,
    EnergySaver,
}

/// <param name="Glyph">Segoe Fluent Icons / Segoe MDL2 Assets glyph.</param>
/// <param name="Ghost">A dimmed full-strength glyph drawn behind <paramref name="Glyph"/> (the empty signal bars); empty for none.</param>
/// <param name="IsDimmed">Drawn in the subtle color: connected, but to nothing useful.</param>
public sealed record NetworkVisual(string Glyph, string Ghost, bool IsDimmed, string ToolTip);

/// <summary>
/// The tray's network glyphs (every one exists in both Segoe Fluent Icons and Segoe MDL2 Assets), the battery icon's
/// fill color, and the tooltips of both.
/// </summary>
public static class TrayIndicators
{
    /// <summary>At or below this, on battery, the fill turns red.</summary>
    public const int CriticalBatteryPercent = 10;

    // Wi-Fi: 1..3 arcs, then full (Wifi1, Wifi2, Wifi3, Wifi); with a warning mark: WifiWarning1..4.
    private static readonly string[] WifiBars = ["\uE872", "\uE873", "\uE874", "\uE701"];
    private static readonly string[] WifiWarningBars = ["\uEB60", "\uEB61", "\uEB62", "\uEB63"];
    private const string WifiFull = "\uE701";

    // Cellular: SignalBars1..5.
    private static readonly string[] CellularBars = ["\uE86C", "\uE86D", "\uE86E", "\uE86F", "\uE870"];
    private const string CellularFull = "\uE870";

    private const string Ethernet = "\uE839";
    private const string EthernetWarning = "\uEB56";
    private const string Globe = "\uE774";
    private const string NetworkOffline = "\uF384";

    public static NetworkVisual For(NetworkState state)
    {
        var online = state.Reach == NetworkReach.Internet;
        var (glyph, ghost, dimmed) = state.Kind switch
        {
            NetworkKind.Unknown => (Globe, "", true),
            NetworkKind.None => (NetworkOffline, "", false),
            NetworkKind.Ethernet => (online ? Ethernet : EthernetWarning, "", false),
            NetworkKind.WiFi when online => (WifiBars[WifiLevel(state.SignalBars)], WifiFull, false),
            NetworkKind.WiFi => (WifiWarningBars[WifiLevel(state.SignalBars)], "", false),
            NetworkKind.Cellular => (CellularBars[CellularLevel(state.SignalBars)], CellularFull, !online),
            _ => (Globe, "", !online),
        };

        // A full-strength glyph needs no ghost behind it.
        return new NetworkVisual(glyph, ghost == glyph ? "" : ghost, dimmed, ToolTip(state));
    }

    public static BatteryFill FillFor(int percent, bool isCharging, bool isPluggedIn, bool isEnergySaverOn) =>
        isCharging || isPluggedIn ? BatteryFill.PluggedIn
        : percent <= CriticalBatteryPercent ? BatteryFill.Critical
        : isEnergySaverOn ? BatteryFill.EnergySaver
        : BatteryFill.Normal;

    /// <summary>Exact percentage, charging state, time left or until full, and energy saver.</summary>
    public static string BatteryToolTip(BatteryState state)
    {
        var percent = Math.Clamp(state.Percent, 0, 100);
        var lines = new List<string>();
        if (state.IsCharging)
        {
            lines.Add($"Charging · {percent}%");
            if (state.TimeToFull is { } toFull)
            {
                lines.Add($"{FormatDuration(toFull)} until fully charged");
            }
        }
        else if (state.IsPluggedIn)
        {
            lines.Add(percent >= 100 ? "Plugged in · fully charged (100%)" : $"Plugged in, not charging · {percent}%");
        }
        else
        {
            lines.Add($"Battery · {percent}%");
            lines.Add(state.TimeRemaining is { } left ? $"{FormatDuration(left)} remaining" : "On battery");
        }

        if (state.IsSaverOn)
        {
            lines.Add("Energy saver is on");
        }

        return string.Join("\n", lines);
    }

    /// <summary>"2 hr 5 min", "45 min", "1 hr", "Less than 1 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var minutes = (int)Math.Round(duration.TotalMinutes);
        if (minutes < 1)
        {
            return "Less than 1 min";
        }

        var hours = minutes / 60;
        minutes %= 60;
        return hours == 0 ? $"{minutes} min" : minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
    }

    private static string ToolTip(NetworkState state)
    {
        if (state.Kind == NetworkKind.Unknown)
        {
            return "Checking network…";
        }

        if (state.Kind == NetworkKind.None)
        {
            return "Not connected\nNo network connection is available";
        }

        var kind = state.Kind switch
        {
            NetworkKind.Ethernet => "Ethernet",
            NetworkKind.WiFi => "Wi-Fi",
            NetworkKind.Cellular => "Cellular",
            _ => "Network",
        };
        var reach = state.Reach switch
        {
            NetworkReach.Internet => "Internet access",
            NetworkReach.Constrained => "Limited connectivity, sign-in may be required",
            _ => "No internet access",
        };

        var lines = new List<string> { string.IsNullOrWhiteSpace(state.Name) ? kind : state.Name, $"{kind} · {reach}" };
        if (state.RoutedThrough is { } via)
        {
            lines.Add($"Routed through {via}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Signal bars (0..5) to the four Wi-Fi glyph strengths; unknown shows full.</summary>
    private static int WifiLevel(int? bars) => bars switch
    {
        null => 3,
        <= 1 => 0,
        2 => 1,
        3 => 2,
        _ => 3,
    };

    private static int CellularLevel(int? bars) => bars is { } b ? Math.Clamp(b, 1, 5) - 1 : 4;
}
