using System.Runtime.InteropServices;

namespace SharpAstro.Serial.Enumeration;

/// <summary>Decoding of the string shapes the Windows device tree hands back; pure, so testable anywhere.</summary>
internal static class DeviceStrings
{
    /// <summary>Splits a REG_MULTI_SZ / DEVPROP string list (UTF-16, NUL-separated, double-NUL-terminated).</summary>
    public static List<string> MultiSz(ReadOnlySpan<byte> bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]);
        var list = new List<string>();
        while (!chars.IsEmpty)
        {
            var end = chars.IndexOf('\0');
            var item = end < 0 ? chars : chars[..end];
            if (item.IsEmpty)
            {
                break;
            }
            list.Add(new string(item));
            chars = end < 0 ? default : chars[(end + 1)..];
        }
        return list;
    }
}
