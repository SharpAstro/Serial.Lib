namespace SharpAstro.Serial;

/// <summary>Port names as the OS spells them, and how two of them compare.</summary>
internal static class PortNames
{
    /// <summary>COM names are case-insensitive on Windows; device paths are case-sensitive elsewhere.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A bare <c>ttyUSB0</c> means <c>/dev/ttyUSB0</c> off Windows; anything else is taken as given.</summary>
    public static string Normalize(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        var name = portName.Trim();
        if (!OperatingSystem.IsWindows() && !name.StartsWith('/'))
        {
            return "/dev/" + name;
        }
        return name;
    }

    /// <summary>The ports the OS lists now, sorted.</summary>
    public static IReadOnlyList<string> Present()
    {
        var names = System.IO.Ports.SerialPort.GetPortNames();
        Array.Sort(names, Comparer);
        return names;
    }
}
