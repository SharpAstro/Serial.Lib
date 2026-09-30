using System.Text;
using SharpAstro.Serial.Enumeration;
using Shouldly;
using Xunit;

namespace SharpAstro.Serial.Tests;

public sealed class EnumerationTests
{
    [Theory]
    [InlineData(@"USB\VID_1A86&PID_7523\5&2A3B4C5D&0&3", 0x1a86, 0x7523, null, false)]   // CH340: no serial, Windows made the instance up
    [InlineData(@"USB\VID_0403&PID_6001\A50285BI", 0x0403, 0x6001, "A50285BI", false)]   // FTDI: its own serial
    [InlineData(@"USB\VID_2341&PID_0043&MI_00\6&1A2B3C&0&0000", 0x2341, 0x0043, null, true)] // one function of a composite device
    [InlineData(@"usb\vid_1a86&pid_7523\abc123", 0x1a86, 0x7523, "abc123", false)]
    public void AUsbInstanceIdNamesVendorProductAndSerial(string id, int vid, int pid, string? serial, bool isInterface)
    {
        UsbInstanceId.TryParse(id, out var parsed).ShouldBeTrue();
        parsed.VendorId.ShouldBe((ushort)vid);
        parsed.ProductId.ShouldBe((ushort)pid);
        parsed.SerialNumber.ShouldBe(serial);
        parsed.IsInterface.ShouldBe(isInterface);
    }

    [Theory]
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000")]   // a vendor bus child: its USB parent carries the identity
    [InlineData(@"ACPI\PNP0501\1")]                              // a motherboard UART
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&1&0&000000000000_00000000")]
    [InlineData("")]
    [InlineData(null)]
    public void ANonUsbInstanceIdIsNotParsed(string? id) => UsbInstanceId.TryParse(id, out _).ShouldBeFalse();

    [Theory]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_VID&000105D6_PID&000A\5&26CB095F&0&83CD1DB5D95D_C00000000", 0x83CD1DB5D95DUL)]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\5&26CB095F&0&000000000000_00000000", 0UL)]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&1&0&000000000000_00000000", 0UL)]
    public void ABluetoothSerialPortNamesTheDeviceAtItsFarEnd(string id, ulong address)
    {
        BluetoothInstanceId.TryParse(id, out var parsed).ShouldBeTrue();
        parsed.Address.ShouldBe(address);
        new SerialBluetoothDevice(parsed.Address).IsIncoming.ShouldBe(address == 0, "Windows' own incoming port has no remote device");
    }

    [Theory]
    [InlineData(@"USB\VID_1A86&PID_7523\5&2A3B4C5D&0&3")]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\5&26CB095F&0&0000_00000000")]  // not twelve digits
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotABluetoothPort(string? id) => BluetoothInstanceId.TryParse(id, out _).ShouldBeFalse();

    [Theory]
    [InlineData(0x240404u, BluetoothMajorClass.AudioVideo)]    // a pair of headphones ("S42", a wearable headset)
    [InlineData(0x001F00u, BluetoothMajorClass.Uncategorized)] // an HC-05 serial module's default
    [InlineData(0x000000u, BluetoothMajorClass.Miscellaneous)]
    [InlineData(0x7A020Cu, BluetoothMajorClass.Phone)]
    [InlineData(0x380104u, BluetoothMajorClass.Computer)]
    public void ADevicesMajorClassIsReadFromItsClassOfDevice(uint cod, BluetoothMajorClass major)
        => new SerialBluetoothDevice(0x83CD1DB5D95D) { ClassOfDevice = cod }.MajorClass.ShouldBe(major);

    [Fact]
    public void AnAddressIsWrittenAsWindowsWritesIt() => new SerialBluetoothDevice(0x83CD1DB5D95D).AddressText.ShouldBe("83cd1db5d95d");

    [Fact]
    public void AStringListSplitsOnItsNuls()
        => DeviceStrings.MultiSz(Encoding.Unicode.GetBytes("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)\0ACPI(_SB_)\0\0"))
            .ShouldBe(["PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)", "ACPI(_SB_)"]);

    [Fact]
    public void ACh340OnLinuxIsKeyedOnItsSocket()
    {
        var fs = new FakeLinuxFs()
            .Link("/dev/serial/by-path/pci-0000:00:14.0-usb-0:3:1.0-port0", "/dev/ttyUSB0")
            .Link("/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0", "/dev/ttyUSB0")
            .Link("/sys/class/tty/ttyUSB0/device", "/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/ttyUSB0")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/idVendor", "1a86")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/idProduct", "7523")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/product", "USB Serial");

        var info = LinuxPortEnumerator.Describe(["/dev/ttyUSB0"], fs).ShouldHaveSingleItem();

        info.VendorId.ShouldBe((ushort)0x1a86);
        info.ProductId.ShouldBe((ushort)0x7523);
        info.SerialNumber.ShouldBeNull();
        info.Description.ShouldBe("USB Serial");
        info.DeviceInstanceId.ShouldBe("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3");
        info.LocationPath.ShouldBe("pci-0000:00:14.0-usb-0:3:1.0-port0");
        info.ById.ShouldBe("usb-1a86_USB_Serial-if00-port0");
        info.Identity.ShouldBe(SerialIdentityKind.Socket);
        info.IdentityKey.ShouldBe("socket:pci-0000:00:14.0-usb-0:3:1.0-port0");
    }

    [Fact]
    public void ACdcAcmDeviceWithASerialIsKeyedOnTheDevice()
    {
        var fs = new FakeLinuxFs()
            .Link("/sys/class/tty/ttyACM0/device", "/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/idVendor", "2341")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/idProduct", "0043")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/manufacturer", "Arduino")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb1/1-2/serial", "95530343434351D031A1");

        var info = LinuxPortEnumerator.Describe(["/dev/ttyACM0"], fs).ShouldHaveSingleItem();

        info.Identity.ShouldBe(SerialIdentityKind.Device);
        info.IdentityKey.ShouldBe("usb:2341:0043:95530343434351D031A1");
        info.Description.ShouldBe("Arduino");
    }

    [Fact]
    public void AMotherboardUartHasOnlyItsName()
    {
        var info = LinuxPortEnumerator.Describe(["/dev/ttyS0"], new FakeLinuxFs()).ShouldHaveSingleItem();

        info.Identity.ShouldBe(SerialIdentityKind.PortName);
        info.IdentityKey.ShouldBe("name:/dev/ttyS0");
    }

    [Fact]
    public void EnumerationListsWhatTheOsListsAndNeverThrows()
    {
        var ports = SerialPorts.Enumerate();

        ports.Select(p => p.PortName).ShouldBe(System.IO.Ports.SerialPort.GetPortNames().Order(PortNames.Comparer), ignoreOrder: true);
    }

    private sealed class FakeLinuxFs : ILinuxFileSystem
    {
        private readonly Dictionary<string, string> _links = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);

        public FakeLinuxFs Link(string path, string target)
        {
            _links[path] = target;
            return this;
        }

        public FakeLinuxFs File(string path, string content)
        {
            _files[path] = content;
            return this;
        }

        public IEnumerable<string> List(string directory)
            => _links.Keys.Concat(_files.Keys).Where(p => p.StartsWith(directory + "/", StringComparison.Ordinal) && p.LastIndexOf('/') == directory.Length);

        public string? ResolveLink(string path) => _links.GetValueOrDefault(path);

        public string? ReadText(string path) => _files.GetValueOrDefault(path);

        public bool FileExists(string path) => _files.ContainsKey(path);
    }
}
