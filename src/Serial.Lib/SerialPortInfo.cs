namespace SharpAstro.Serial;

/// <summary>What a port's identity is keyed on, strongest first.</summary>
public enum SerialIdentityKind
{
    /// <summary>
    /// Nothing better than the OS name. On Windows a USB serial port's COM name follows the USB SOCKET, and on
    /// Linux a <c>/dev/ttyUSBn</c> name follows enumeration order, so this identity is the weakest.
    /// </summary>
    PortName,

    /// <summary>
    /// The USB socket (the port's location path). Survives re-enumeration and renames, but two identical devices
    /// swapped between sockets swap identities; a chip with no serial number (a CH340) can do no better.
    /// </summary>
    Socket,

    /// <summary>The device itself: USB vendor, product and serial number. Survives moving it to another socket.</summary>
    Device,
}

/// <summary>A port present on this machine, with the stable identity the OS offers for it.</summary>
/// <param name="PortName">The OS name to open it by (<c>COM3</c>, <c>/dev/ttyUSB0</c>).</param>
public sealed record SerialPortInfo(string PortName)
{
    /// <summary>The OS's description, e.g. <c>USB-SERIAL CH340 (COM3)</c>.</summary>
    public string? Description { get; init; }

    /// <summary>The USB vendor id, when the port is a USB device.</summary>
    public ushort? VendorId { get; init; }

    /// <summary>The USB product id, when the port is a USB device.</summary>
    public ushort? ProductId { get; init; }

    /// <summary>The USB serial number, when the chip reports one (FTDI and most CDC-ACM parts do, a CH340 does not).</summary>
    public string? SerialNumber { get; init; }

    /// <summary>The OS's own id for the device: a Windows device instance id, or a Linux sysfs device path.</summary>
    public string? DeviceInstanceId { get; init; }

    /// <summary>
    /// The USB socket the device is plugged into: a Windows location path
    /// (<c>PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)</c>) or a Linux <c>/dev/serial/by-path</c> name.
    /// </summary>
    public string? LocationPath { get; init; }

    /// <summary>The Linux <c>/dev/serial/by-id</c> name, when udev made one.</summary>
    public string? ById { get; init; }

    /// <summary>
    /// What a Bluetooth serial port leads to (Windows): the paired device at its far end, with the name and class Windows
    /// recorded for it, or Windows' own incoming port. Null for any other port. A caller probing ports for instruments can
    /// tell a paired headset (<see cref="BluetoothMajorClass.AudioVideo"/>), whose serial channel takes every write and
    /// answers none, from a serial module.
    /// </summary>
    public SerialBluetoothDevice? Bluetooth { get; init; }

    /// <summary>The strongest identity available for this port.</summary>
    public SerialIdentityKind Identity
        => VendorId is not null && ProductId is not null && !string.IsNullOrEmpty(SerialNumber) ? SerialIdentityKind.Device
        : !string.IsNullOrEmpty(LocationPath) ? SerialIdentityKind.Socket
        : SerialIdentityKind.PortName;

    /// <summary>
    /// A string that names this port's device (or socket, or name, per <see cref="Identity"/>) stably, for keying a
    /// saved configuration: <c>usb:1a86:7523:SERIAL</c>, <c>socket:LOCATION</c> or <c>name:COM3</c>.
    /// </summary>
    public string IdentityKey => Identity switch
    {
        SerialIdentityKind.Device => $"usb:{VendorId:x4}:{ProductId:x4}:{SerialNumber}",
        SerialIdentityKind.Socket => $"socket:{LocationPath}",
        _ => $"name:{PortName}",
    };
}
