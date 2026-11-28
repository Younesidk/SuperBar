using SuperBar.Core.Shell;
using SuperBar.Windows.Bluetooth;

namespace SuperBar.Tests.Windows;

/// <summary>The pure parts of the PnP Bluetooth battery service: joining container probes and picking icons.</summary>
public class BluetoothBatteryLogicTests
{
    private static readonly Guid A = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid B = new("11111111-0000-0000-0000-000000000002");
    private static readonly Guid C = new("11111111-0000-0000-0000-000000000003");

    private static ContainerProbe Probe(Guid id, string name, params int[] readings)
    {
        var probe = new ContainerProbe(id, name) { IsBluetooth = true };
        probe.Readings.AddRange(readings);
        return probe;
    }

    [Fact]
    public void Containers_without_a_valid_reading_are_left_out()
    {
        var devices = BluetoothBatteryMerger.Merge([Probe(A, "No battery"), Probe(B, "Broken", 255, -1), Probe(C, "Good", 50)]);

        Assert.Equal("Good", Assert.Single(devices).Name);
    }

    [Fact]
    public void Non_bluetooth_containers_are_left_out()
    {
        var usb = Probe(A, "USB thing", 50);
        usb.IsBluetooth = false;

        Assert.Empty(BluetoothBatteryMerger.Merge([usb]));
    }

    [Fact]
    public void Several_readings_of_one_container_become_one_device_with_the_lowest_valid_level()
    {
        var devices = BluetoothBatteryMerger.Merge([Probe(A, "Buds", 80, 120, 75), Probe(A, "", 90)]);

        var device = Assert.Single(devices);
        Assert.Equal((A.ToString("D"), "Buds", 75), (device.Id, device.Name, device.BatteryPercent));
    }

    [Fact]
    public void Devices_are_sorted_by_level_then_name_then_id()
    {
        var devices = BluetoothBatteryMerger.Merge([Probe(C, "zeta", 40), Probe(A, "Alpha", 90), Probe(B, "beta", 40)]);

        Assert.Equal(["beta", "zeta", "Alpha"], devices.Select(d => d.Name));
    }

    [Fact]
    public void A_nameless_device_gets_a_generic_name()
    {
        Assert.Equal("Bluetooth device", Assert.Single(BluetoothBatteryMerger.Merge([Probe(A, "  ", 10)])).Name);
    }

    [Fact]
    public void The_kind_comes_from_the_probe()
    {
        var headset = Probe(A, "TruSound", 60);
        headset.ClassOfDevice = 0x240404; // audio/video, wearable headset (as reported by a real device)
        var mouse = Probe(B, "Haste 2 Core", 100);
        mouse.HasMouse = mouse.HasKeyboard = true;

        var devices = BluetoothBatteryMerger.Merge([headset, mouse]);

        Assert.Equal([BluetoothDeviceKind.Headphones, BluetoothDeviceKind.Mouse], devices.Select(d => d.Kind));
    }

    [Theory]
    [InlineData(0x240404, BluetoothDeviceKind.Headphones)] // headset
    [InlineData(0x200408, BluetoothDeviceKind.Headphones)] // hands-free
    [InlineData(0x240418, BluetoothDeviceKind.Headphones)] // headphones
    [InlineData(0x240414, BluetoothDeviceKind.Speaker)] // loudspeaker
    [InlineData(0x240428, BluetoothDeviceKind.Speaker)] // HiFi audio
    [InlineData(0x000540, BluetoothDeviceKind.Keyboard)]
    [InlineData(0x000580, BluetoothDeviceKind.Mouse)]
    [InlineData(0x0005C0, BluetoothDeviceKind.Keyboard)] // keyboard + pointing combo
    [InlineData(0x000508, BluetoothDeviceKind.Gamepad)]
    [InlineData(0x000504, BluetoothDeviceKind.Gamepad)] // joystick
    [InlineData(0x5A020C, BluetoothDeviceKind.Unknown)] // a phone
    [InlineData(0, BluetoothDeviceKind.Unknown)]
    public void The_class_of_device_decides_the_kind(int cod, BluetoothDeviceKind expected) =>
        Assert.Equal(expected, BluetoothDeviceClassifier.FromClassOfDevice(cod));

    [Fact]
    public void Earbuds_are_recognized_by_name_even_though_they_report_a_headset_class()
    {
        Assert.Equal(BluetoothDeviceKind.Earbuds, BluetoothDeviceClassifier.Classify("Galaxy Buds2 Pro", 0x240404, false, false));
        Assert.Equal(BluetoothDeviceKind.Earbuds, BluetoothDeviceClassifier.Classify("AirPods Pro", 0x240418, false, false));
    }

    [Fact]
    public void Without_a_class_the_name_and_then_the_node_classes_decide()
    {
        Assert.Equal(BluetoothDeviceKind.Gamepad, BluetoothDeviceClassifier.Classify("Xbox Wireless Controller", null, false, true));
        Assert.Equal(BluetoothDeviceKind.Mouse, BluetoothDeviceClassifier.Classify("Haste 2 Core", null, true, true));
        Assert.Equal(BluetoothDeviceKind.Keyboard, BluetoothDeviceClassifier.Classify("K380", null, false, true));
        Assert.Equal(BluetoothDeviceKind.Speaker, BluetoothDeviceClassifier.Classify("JBL Speaker", null, false, false));
        Assert.Equal(BluetoothDeviceKind.Unknown, BluetoothDeviceClassifier.Classify("Gizmo", null, false, false));
    }
}
