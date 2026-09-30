using System.Globalization;

namespace SharpAstro.Serial.Enumeration;

/// <summary>
/// A Windows Bluetooth serial port's device instance id, e.g.
/// <c>BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_VID&amp;000105D6_PID&amp;000A\5&amp;26CB095F&amp;0&amp;83CD1DB5D95D_C00000000</c>:
/// the service is the Serial Port Profile's UUID, and the instance part ends in the remote device's 48-bit address, then an
/// underscore. Windows' own incoming port (<c>..._LOCALMFG&amp;0000\...&amp;000000000000_00000000</c>) carries the address 0.
/// </summary>
/// <param name="Address">The remote device's address; 0 for the incoming port.</param>
internal readonly record struct BluetoothInstanceId(ulong Address)
{
    public static bool TryParse(string? instanceId, out BluetoothInstanceId parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(instanceId))
        {
            return false;
        }
        var parts = instanceId.Split('\\');
        if (parts.Length != 3 || !parts[0].Equals("BTHENUM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The address is the last ampersand-separated field before the final underscore.
        var instance = parts[2];
        var underscore = instance.LastIndexOf('_');
        if (underscore <= 0)
        {
            return false;
        }
        var ampersand = instance.LastIndexOf('&', underscore - 1);
        var text = instance.AsSpan(ampersand + 1, underscore - ampersand - 1);
        if (text.Length != 12 || !ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
        {
            return false;
        }

        parsed = new BluetoothInstanceId(address);
        return true;
    }
}
