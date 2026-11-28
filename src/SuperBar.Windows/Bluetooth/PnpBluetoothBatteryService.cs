using System.Globalization;
using Microsoft.Extensions.Logging;
using SuperBar.Core.Shell;
using Windows.Devices.Enumeration;

namespace SuperBar.Windows.Bluetooth;

/// <summary>
/// <see cref="IBluetoothBatteryService"/> over the Windows PnP device index, never over a connection of its own (no
/// GATT reads, nothing that wakes, scans for or pairs a device). Two steps:
/// <list type="number">
/// <item>The paired, connected device containers (one per physical device; Classic and LE alike).</item>
/// <item>For each container, its PnP device nodes: the battery level property (<see cref="BatteryLevelKey"/>, the
/// value Settings shows), the Bluetooth address (proof it is a Bluetooth device), the class of device and the device
/// setup class (for the icon). The battery property usually sits on a child node (the Hands-Free profile's
/// <c>System</c> node, or the LE device node next to its <c>HIDClass</c> children), so nodes are matched by container
/// rather than by class.</item>
/// </list>
/// <para>
/// Why containers and not association endpoints (<c>System.Devices.Aep.IsConnected</c>): an AEP query over the
/// Bluetooth protocols waits for the radio's enumeration to finish, which takes about 30 s even for paired devices,
/// while the container index answers in well under 100 ms. Both report the same connection state.
/// </para>
/// Missing radios, Bluetooth turned off and devices disconnecting mid-query all end in an empty (or shorter) list.
/// </summary>
internal sealed class PnpBluetoothBatteryService(ILogger<PnpBluetoothBatteryService> logger) : IBluetoothBatteryService
{
    /// <summary>DEVPKEY_Bluetooth_Battery: the battery level in percent (a byte).</summary>
    internal const string BatteryLevelKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>DEVPKEY_Bluetooth_DeviceAddress: set on the Bluetooth device node of Classic and LE devices.</summary>
    internal const string DeviceAddressKey = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1";

    /// <summary>DEVPKEY_Bluetooth_ClassOfDevice: the 24-bit class of device of a Classic device.</summary>
    internal const string ClassOfDeviceKey = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 10";

    private const string ClassGuidKey = "System.Devices.ClassGuid";

    private const string ConnectedPairedContainers =
        "System.Devices.Connected:=System.StructuredQueryType.Boolean#True AND System.Devices.Paired:=System.StructuredQueryType.Boolean#True";

    private static readonly Guid MouseClass = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    private static readonly Guid KeyboardClass = new("4d36e96b-e325-11ce-bfc1-08002be10318");
    private static readonly string[] NodeProperties = [BatteryLevelKey, DeviceAddressKey, ClassOfDeviceKey, ClassGuidKey];

    public async Task<IReadOnlyList<BluetoothBatteryDevice>> GetConnectedDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var containers = await DeviceInformation
                .FindAllAsync(ConnectedPairedContainers, [], DeviceInformationKind.DeviceContainer)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var probes = new List<ContainerProbe>();
            foreach (var container in containers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(container.Id, out var id) && id != Guid.Empty
                    && await ProbeAsync(id, container.Name ?? "", cancellationToken).ConfigureAwait(false) is { } probe)
                {
                    probes.Add(probe);
                }
            }

            return BluetoothBatteryMerger.Merge(probes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No radio, Bluetooth off, the device stack restarting: nothing to show right now.
            logger.LogDebug(ex, "Could not enumerate Bluetooth battery levels");
            return [];
        }
    }

    private async Task<ContainerProbe?> ProbeAsync(Guid container, string name, CancellationToken cancellationToken)
    {
        try
        {
            var filter = $"System.Devices.ContainerId:=\"{container.ToString("B", CultureInfo.InvariantCulture)}\"";
            var nodes = await DeviceInformation
                .FindAllAsync(filter, NodeProperties, DeviceInformationKind.Device)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            var probe = new ContainerProbe(container, name);
            foreach (var node in nodes)
            {
                probe.IsBluetooth |= Read(node, DeviceAddressKey) is string { Length: > 0 };
                if (AsInt(Read(node, BatteryLevelKey)) is { } percent)
                {
                    probe.Readings.Add(percent);
                }

                if (AsInt(Read(node, ClassOfDeviceKey)) is { } cod && cod != 0)
                {
                    probe.ClassOfDevice ??= cod;
                }

                if (Read(node, ClassGuidKey) is Guid setupClass)
                {
                    probe.HasMouse |= setupClass == MouseClass;
                    probe.HasKeyboard |= setupClass == KeyboardClass;
                }
            }

            return probe;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The device disconnected between the two queries.
            logger.LogDebug(ex, "Could not read the device nodes of container {Container}", container);
            return null;
        }
    }

    private static object? Read(DeviceInformation device, string key) =>
        device.Properties.TryGetValue(key, out var value) ? value : null;

    private static int? AsInt(object? value) => value switch
    {
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        short s => s,
        uint ui when ui <= int.MaxValue => (int)ui,
        int i => i,
        _ => null,
    };
}

/// <summary>What the PnP nodes of one connected device container said.</summary>
internal sealed class ContainerProbe(Guid containerId, string name)
{
    public Guid ContainerId { get; } = containerId;

    public string Name { get; } = name;

    /// <summary>One of the nodes carries a Bluetooth address.</summary>
    public bool IsBluetooth { get; set; }

    /// <summary>Every battery level a node of the container reported (not yet validated).</summary>
    public List<int> Readings { get; } = new();

    /// <summary>The 24-bit class of device of a Classic device.</summary>
    public int? ClassOfDevice { get; set; }

    /// <summary>A node is in the Mouse setup class.</summary>
    public bool HasMouse { get; set; }

    /// <summary>A node is in the Keyboard setup class (mice often have one too, for their extra buttons).</summary>
    public bool HasKeyboard { get; set; }
}

/// <summary>Turns container probes into devices. Pure, so it is tested headlessly.</summary>
internal static class BluetoothBatteryMerger
{
    /// <summary>
    /// One device per Bluetooth container with at least one valid (0..100) reading. When several nodes report, the
    /// lowest valid value wins: the conservative answer for a pair of earbuds or a stale node. Duplicate containers are
    /// merged. Sorted lowest level first, then by name, then by id, so the order never flickers.
    /// </summary>
    public static IReadOnlyList<BluetoothBatteryDevice> Merge(IEnumerable<ContainerProbe> probes) =>
        probes
            .Where(p => p.IsBluetooth)
            .GroupBy(p => p.ContainerId)
            .Select(g =>
            {
                var levels = g.SelectMany(p => p.Readings).Where(r => r is >= 0 and <= 100).ToList();
                if (levels.Count == 0)
                {
                    return null;
                }

                var name = g.Select(p => p.Name.Trim()).FirstOrDefault(n => n.Length > 0) ?? "Bluetooth device";
                var kind = BluetoothDeviceClassifier.Classify(
                    name,
                    g.Select(p => p.ClassOfDevice).FirstOrDefault(c => c is not null),
                    g.Any(p => p.HasMouse),
                    g.Any(p => p.HasKeyboard));
                return new BluetoothBatteryDevice(g.Key.ToString("D", CultureInfo.InvariantCulture), name, levels.Min(), kind);
            })
            .OfType<BluetoothBatteryDevice>()
            .OrderBy(d => d.BatteryPercent)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Picks a device's icon from its Bluetooth class of device, its PnP node classes and its name.</summary>
internal static class BluetoothDeviceClassifier
{
    private const int AudioVideoMajor = 0x04;
    private const int PeripheralMajor = 0x05;

    /// <param name="name">The device's friendly name.</param>
    /// <param name="classOfDevice">The 24-bit class of device of a Classic device (major in bits 12..8, minor in bits 7..2).</param>
    /// <param name="hasMouseNode">A PnP node of the device is a mouse.</param>
    /// <param name="hasKeyboardNode">A PnP node of the device is a keyboard.</param>
    public static BluetoothDeviceKind Classify(string? name, int? classOfDevice, bool hasMouseNode, bool hasKeyboardNode)
    {
        // Earbuds report themselves as a headset or headphones; only their name tells them apart.
        var byName = FromName(name);
        if (byName == BluetoothDeviceKind.Earbuds)
        {
            return byName;
        }

        var byClass = FromClassOfDevice(classOfDevice);
        if (byClass != BluetoothDeviceKind.Unknown)
        {
            return byClass;
        }

        if (byName != BluetoothDeviceKind.Unknown)
        {
            return byName;
        }

        // Mice usually add a keyboard node for their extra buttons, so a mouse node decides first.
        return hasMouseNode ? BluetoothDeviceKind.Mouse
            : hasKeyboardNode ? BluetoothDeviceKind.Keyboard
            : BluetoothDeviceKind.Unknown;
    }

    internal static BluetoothDeviceKind FromClassOfDevice(int? classOfDevice)
    {
        if (classOfDevice is not { } cod || cod <= 0)
        {
            return BluetoothDeviceKind.Unknown;
        }

        var major = (cod >> 8) & 0x1F;
        var minor = (cod >> 2) & 0x3F;
        switch (major)
        {
            case AudioVideoMajor:
                return minor switch
                {
                    0x01 or 0x02 or 0x06 => BluetoothDeviceKind.Headphones, // wearable headset, hands-free, headphones
                    0x05 or 0x07 or 0x0A => BluetoothDeviceKind.Speaker, // loudspeaker, portable audio, HiFi audio
                    _ => BluetoothDeviceKind.Unknown,
                };
            case PeripheralMajor:
                switch ((minor >> 4) & 0x3)
                {
                    case 0x1:
                    case 0x3: // keyboard + pointing device combo
                        return BluetoothDeviceKind.Keyboard;
                    case 0x2:
                        return BluetoothDeviceKind.Mouse;
                }

                return (minor & 0xF) is 0x1 or 0x2 ? BluetoothDeviceKind.Gamepad : BluetoothDeviceKind.Unknown; // joystick, gamepad
            default:
                return BluetoothDeviceKind.Unknown;
        }
    }

    internal static BluetoothDeviceKind FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BluetoothDeviceKind.Unknown;
        }

        var n = name.ToLowerInvariant();
        if (Has(n, "buds", "airpods", "earbud", "tws"))
        {
            return BluetoothDeviceKind.Earbuds;
        }

        if (Has(n, "headphone", "headset", "hands-free", "wh-1000", "quietcomfort"))
        {
            return BluetoothDeviceKind.Headphones;
        }

        if (Has(n, "mouse", "mx master", "mx anywhere", "trackball"))
        {
            return BluetoothDeviceKind.Mouse;
        }

        if (Has(n, "keyboard"))
        {
            return BluetoothDeviceKind.Keyboard;
        }

        if (Has(n, "controller", "gamepad", "dualsense", "dualshock", "joy-con"))
        {
            return BluetoothDeviceKind.Gamepad;
        }

        return Has(n, "speaker", "soundbar", "soundlink", "boombox")
            ? BluetoothDeviceKind.Speaker
            : BluetoothDeviceKind.Unknown;
    }

    private static bool Has(string name, params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.Ordinal));
}
