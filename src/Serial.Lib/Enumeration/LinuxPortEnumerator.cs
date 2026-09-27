namespace SharpAstro.Serial.Enumeration;

/// <summary>
/// The file-system reads the Linux enumeration needs, behind a seam so a test can hand it a fake <c>/sys</c> and
/// <c>/dev/serial</c> tree.
/// </summary>
internal interface ILinuxFileSystem
{
    /// <summary>The full paths of a directory's entries; empty when it does not exist.</summary>
    IEnumerable<string> List(string directory);

    /// <summary>A link's final target as an absolute path, or null when it is not a link or does not resolve.</summary>
    string? ResolveLink(string path);

    /// <summary>A small text file's content, trimmed, or null when it cannot be read.</summary>
    string? ReadText(string path);

    bool FileExists(string path);
}

internal sealed class LinuxFileSystem : ILinuxFileSystem
{
    public static LinuxFileSystem Instance { get; } = new LinuxFileSystem();

    public IEnumerable<string> List(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory) : [];

    public string? ResolveLink(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>
/// Describes the present ports from sysfs and udev's <c>/dev/serial</c> links. A <c>/dev/ttyUSBn</c> name follows
/// enumeration order, so it is not even stable per socket (tianwen #784); the <c>by-path</c> link names the socket
/// and the <c>by-id</c> link the device, and sysfs has the USB vendor, product and serial number.
/// </summary>
internal static class LinuxPortEnumerator
{
    public static IReadOnlyList<SerialPortInfo> Describe(IReadOnlyList<string> names, ILinuxFileSystem fs)
    {
        var byId = LinksByTarget(fs, "/dev/serial/by-id");
        var byPath = LinksByTarget(fs, "/dev/serial/by-path");
        return [.. names.Select(name => Describe(name, fs, byId, byPath))];
    }

    private static SerialPortInfo Describe(string name, ILinuxFileSystem fs, Dictionary<string, string> byId, Dictionary<string, string> byPath)
    {
        var info = new SerialPortInfo(name)
        {
            ById = byId.GetValueOrDefault(name),
            LocationPath = byPath.GetValueOrDefault(name),
        };

        // /sys/class/tty/<tty>/device points into the device tree (the interface for ttyACM, the usb-serial port
        // for ttyUSB); the USB device is the nearest ancestor with an idVendor file.
        var tty = Path.GetFileName(name);
        if (fs.ResolveLink($"/sys/class/tty/{tty}/device") is not { } device)
        {
            return info;
        }
        // Walked with '/' by hand: Path.GetDirectoryName would turn the separators to '\' when this runs (as its
        // tests do) on Windows.
        for (var dir = device; dir.Length > "/sys/devices".Length; dir = dir[..Math.Max(0, dir.LastIndexOf('/'))])
        {
            var vendorFile = $"{dir}/idVendor";
            if (!fs.FileExists(vendorFile))
            {
                continue;
            }
            var manufacturer = fs.ReadText($"{dir}/manufacturer");
            var product = fs.ReadText($"{dir}/product");
            return info with
            {
                VendorId = Hex(fs.ReadText(vendorFile)),
                ProductId = Hex(fs.ReadText($"{dir}/idProduct")),
                SerialNumber = fs.ReadText($"{dir}/serial") is { Length: > 0 } serial ? serial : null,
                DeviceInstanceId = dir,
                Description = string.Join(' ', new[] { manufacturer, product }.Where(static s => !string.IsNullOrEmpty(s))) is { Length: > 0 } d ? d : null,
            };
        }
        return info with { DeviceInstanceId = device };
    }

    private static Dictionary<string, string> LinksByTarget(ILinuxFileSystem fs, string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in fs.List(directory))
        {
            if (fs.ResolveLink(link) is { } target)
            {
                map[target] = Path.GetFileName(link);
            }
        }
        return map;
    }

    private static ushort? Hex(string? text)
        => ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}
