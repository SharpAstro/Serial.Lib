using System.Globalization;

namespace SharpAstro.Serial.Enumeration;

/// <summary>
/// A Windows USB device instance id, e.g. <c>USB\VID_1A86&amp;PID_7523\5&amp;2A3B4C5D&amp;0&amp;3</c>: the hardware part
/// names vendor and product (and the interface, <c>MI_nn</c>, for one function of a composite device), and the
/// instance part is the device's own serial number when it has a usable one. When it has none Windows makes the
/// instance part up from the port path, and that always contains an ampersand.
/// </summary>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
/// <param name="SerialNumber">The device's serial number, or null when Windows generated the instance part.</param>
/// <param name="IsInterface">True for one function of a composite device (<c>MI_nn</c>); its serial number and
/// socket belong to the parent device node.</param>
internal readonly record struct UsbInstanceId(ushort VendorId, ushort ProductId, string? SerialNumber, bool IsInterface)
{
    public static bool TryParse(string? instanceId, out UsbInstanceId parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(instanceId))
        {
            return false;
        }
        var parts = instanceId.Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ushort? vid = null, pid = null;
        var isInterface = false;
        foreach (var token in parts[1].Split('&'))
        {
            if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && TryHex(token.AsSpan(4), out var v))
            {
                vid = v;
            }
            else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && TryHex(token.AsSpan(4), out var p))
            {
                pid = p;
            }
            else if (token.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
            {
                isInterface = true;
            }
        }
        if (vid is not { } vendor || pid is not { } product)
        {
            return false;
        }

        var instance = parts[2];
        var serial = isInterface || instance.Length == 0 || instance.Contains('&') ? null : instance;
        parsed = new UsbInstanceId(vendor, product, serial, isInterface);
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> text, out ushort value)
        => ushort.TryParse(text.Length > 4 ? text[..4] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}
