using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using Windows.Networking.Connectivity;
using NetInterface = System.Net.NetworkInformation.NetworkInterface;
using NetInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType;

namespace SuperBar.Windows.SystemStatus;

/// <summary>
/// <see cref="INetworkStateService"/> over <see cref="NetworkInformation"/>: the internet connection profile (Windows'
/// default route) and every connected profile, re-read whenever <see cref="NetworkInformation.NetworkStatusChanged"/>
/// fires. Virtual adapters (Hyper-V, WSL, Docker, VMs, VPNs, Tailscale…) are recognized by interface type and by the
/// adapter description <see cref="NetInterface"/> reports, so they never pass for the physical link; see
/// <see cref="NetworkStateResolver"/> for how the shown connection is picked.
/// </summary>
internal sealed class WinRtNetworkStateService(IUiDispatcher dispatcher, ILogger<WinRtNetworkStateService> logger)
    : WatchedState<NetworkState>(dispatcher, logger, NetworkState.Unknown, TimeSpan.FromMilliseconds(300)), INetworkStateService
{
    // IANA ifType values (https://www.iana.org/assignments/ianaiftype-mib).
    private const uint IanaEthernet = 6;
    private const uint IanaWiFi = 71;
    private const uint IanaLoopback = 24;
    private const uint IanaPropVirtual = 53;
    private const uint IanaTunnel = 131;
    private const uint IanaMobileBroadbandGsm = 243;
    private const uint IanaMobileBroadbandCdma = 244;

    /// <summary>Adapter description or name fragments of virtual switches, VMs, containers and tunnels.</summary>
    private static readonly string[] VirtualMarkers =
    [
        "Hyper-V", "vEthernet", "Virtual", "VMware", "VirtualBox", "WSL", "Docker", "Loopback", "Npcap",
        "TAP-", "Tunnel", "WireGuard", "Wintun", "Tailscale", "ZeroTier", "OpenVPN", "VPN", "WAN Miniport",
        "Fortinet", "AnyConnect", "Juniper", "NordLynx", "ExpressVPN", "Mullvad",
    ];

    protected override void Attach() => NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

    protected override void Detach() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;

    private void OnNetworkStatusChanged(object sender) => RequestRead();

    protected override NetworkState Read()
    {
        var internet = NetworkInformation.GetInternetConnectionProfile();
        var internetKey = internet is null ? null : Key(internet);
        var adapters = ReadAdapterDescriptions();

        var candidates = new List<NetworkCandidate>();
        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            if (Describe(profile, internetKey, adapters) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        // Not normally possible (the internet profile is one of the connection profiles), but never lose it.
        if (internet is not null && !candidates.Any(c => c.IsInternetProfile) && Describe(internet, internetKey, adapters) is { } own)
        {
            candidates.Add(own);
        }

        var state = NetworkStateResolver.Resolve(candidates);
        Logger.LogDebug("Network: {Kind} {Reach} {Name} (bars {Bars}, via {Via}); {Count} connected profile(s)", state.Kind, state.Reach, state.Name, state.SignalBars, state.RoutedThrough, candidates.Count);
        return state;
    }

    private NetworkCandidate? Describe(ConnectionProfile profile, string? internetKey, IReadOnlyDictionary<Guid, (string Text, bool IsTunnel)> adapters)
    {
        try
        {
            var reach = profile.GetNetworkConnectivityLevel() switch
            {
                NetworkConnectivityLevel.InternetAccess => NetworkReach.Internet,
                NetworkConnectivityLevel.ConstrainedInternetAccess => NetworkReach.Constrained,
                NetworkConnectivityLevel.LocalAccess => NetworkReach.LocalOnly,
                _ => NetworkReach.Disconnected,
            };
            if (reach == NetworkReach.Disconnected)
            {
                return null;
            }

            var adapter = profile.NetworkAdapter;
            var iana = adapter?.IanaInterfaceType ?? 0;
            var kind = profile.IsWlanConnectionProfile || iana == IanaWiFi ? NetworkKind.WiFi
                : profile.IsWwanConnectionProfile || iana is IanaMobileBroadbandGsm or IanaMobileBroadbandCdma ? NetworkKind.Cellular
                : iana == IanaEthernet ? NetworkKind.Ethernet
                : NetworkKind.Other;

            var isVirtual = iana is IanaLoopback or IanaPropVirtual or IanaTunnel;
            if (adapter is not null && adapters.TryGetValue(adapter.NetworkAdapterId, out var info))
            {
                isVirtual |= info.IsTunnel || VirtualMarkers.Any(m => info.Text.Contains(m, StringComparison.OrdinalIgnoreCase));
            }

            int? bars = null;
            if (kind is NetworkKind.WiFi or NetworkKind.Cellular && profile.GetSignalBars() is { } signal)
            {
                bars = signal;
            }

            return new NetworkCandidate(kind, reach, NameOf(profile, kind), bars, isVirtual, internetKey is not null && Key(profile) == internetKey);
        }
        catch (Exception ex)
        {
            // A profile that disappears mid-read (adapter unplugged) throws; it is simply left out.
            Logger.LogDebug(ex, "Could not read a connection profile");
            return null;
        }
    }

    /// <summary>The SSID for Wi-Fi (the profile name when Windows withholds it, e.g. without location permission), otherwise the profile name.</summary>
    private static string NameOf(ConnectionProfile profile, NetworkKind kind)
    {
        if (kind == NetworkKind.WiFi)
        {
            try
            {
                if (profile.WlanConnectionProfileDetails?.GetConnectedSsid() is { Length: > 0 } ssid)
                {
                    return ssid;
                }
            }
            catch (Exception)
            {
                // Fall back to the profile name, which Windows sets to the SSID for Wi-Fi profiles.
            }
        }

        return profile.ProfileName ?? "";
    }

    /// <summary>Identifies a profile across separate reads: the same profile object is not returned twice.</summary>
    private static string Key(ConnectionProfile profile) => $"{profile.NetworkAdapter?.NetworkAdapterId}|{profile.ProfileName}";

    /// <summary>Description and name of every adapter, by adapter id, and whether .NET sees it as a tunnel or loopback.</summary>
    private IReadOnlyDictionary<Guid, (string Text, bool IsTunnel)> ReadAdapterDescriptions()
    {
        var result = new Dictionary<Guid, (string, bool)>();
        try
        {
            foreach (var nic in NetInterface.GetAllNetworkInterfaces())
            {
                if (Guid.TryParse(nic.Id, out var id))
                {
                    result[id] = ($"{nic.Description} {nic.Name}", nic.NetworkInterfaceType is NetInterfaceType.Tunnel or NetInterfaceType.Loopback);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not list network adapters");
        }

        return result;
    }
}
