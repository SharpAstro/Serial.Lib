namespace SharpAstro.Serial;

/// <summary>The major device class a Bluetooth device reports: bits 8 to 12 of its Class of Device (Bluetooth Assigned Numbers).</summary>
public enum BluetoothMajorClass : byte
{
    /// <summary>No class given, or a device that fits no other.</summary>
    Miscellaneous = 0x00,

    /// <summary>A desktop, laptop, handheld PC.</summary>
    Computer = 0x01,

    /// <summary>A mobile phone, a cordless phone, a modem.</summary>
    Phone = 0x02,

    /// <summary>A LAN or network access point.</summary>
    Network = 0x03,

    /// <summary>A headset, headphones, a speaker, a car kit, a microphone.</summary>
    AudioVideo = 0x04,

    /// <summary>A keyboard, a mouse, a pen, a game controller.</summary>
    Peripheral = 0x05,

    /// <summary>A printer, a scanner, a camera, a display.</summary>
    Imaging = 0x06,

    /// <summary>A watch, glasses, a pager.</summary>
    Wearable = 0x07,

    /// <summary>A toy, a game.</summary>
    Toy = 0x08,

    /// <summary>A health monitor.</summary>
    Health = 0x09,

    /// <summary>A device that declines to say: the default of many serial modules (an HC-05 among them).</summary>
    Uncategorized = 0x1F,
}

/// <summary>
/// What a Bluetooth serial port leads to: the paired device at its far end, as Windows recorded it when it was paired, or
/// Windows' own INCOMING port (<see cref="IsIncoming"/>), which waits for a remote device to dial it and has nobody to
/// answer a host that writes to it.
/// </summary>
/// <param name="Address">The remote device's 48-bit Bluetooth address; 0 for the incoming port.</param>
public sealed record SerialBluetoothDevice(ulong Address)
{
    /// <summary>Windows' incoming Bluetooth serial port: no remote device, so a write to it is never answered.</summary>
    public bool IsIncoming => Address == 0;

    /// <summary>The name the device gave when it was paired (<c>S42</c>, <c>HC-05</c>), when Windows kept one.</summary>
    public string? Name { get; init; }

    /// <summary>The device's Class of Device as Windows recorded it, when it did.</summary>
    public uint? ClassOfDevice { get; init; }

    /// <summary>The major class within <see cref="ClassOfDevice"/>: <see cref="BluetoothMajorClass.AudioVideo"/> for headphones.</summary>
    public BluetoothMajorClass? MajorClass => ClassOfDevice is { } cod ? (BluetoothMajorClass)((cod >> 8) & 0x1F) : null;

    /// <summary>The address as Windows writes it, twelve hex digits (<c>83cd1db5d95d</c>).</summary>
    public string AddressText => Address.ToString("x12", System.Globalization.CultureInfo.InvariantCulture);
}
