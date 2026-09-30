using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SharpAstro.Serial.Enumeration;

/// <summary>
/// Describes the present COM ports from the device tree: every device of the Ports setup class, its <c>PortName</c>,
/// its description, and, walking up to the physical USB device node, the vendor, product, serial number and the
/// socket's location path. A COM name follows the USB socket (swap two identical units and their names swap), so
/// the serial number, or failing it the location path, is what identity should key on (tianwen #783).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsPortEnumerator
{
    public static IReadOnlyList<SerialPortInfo> Describe(IReadOnlyList<string> names)
    {
        Dictionary<string, SerialPortInfo> described;
        try
        {
            described = DescribePortsClass();
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Never fail an enumeration for want of detail: list by name.
            described = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        }
        return [.. names.Select(n => described.TryGetValue(n, out var info) ? info : new SerialPortInfo(n))];
    }

    private static Dictionary<string, SerialPortInfo> DescribePortsClass()
    {
        var result = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        var portsClass = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");
        var set = SetupDiGetClassDevsW(&portsClass, null, 0, DIGCF_PRESENT);
        if (set == InvalidHandle)
        {
            return result;
        }
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, &data) != 0; i++)
            {
                if (ReadPortName(set, &data) is not { } portName)
                {
                    continue;
                }
                result[portName] = Describe(portName, data.DevInst, ReadFriendlyName(set, &data));
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static SerialPortInfo Describe(string portName, uint devInst, string? description)
    {
        var instanceId = DeviceId(devInst);
        ushort? vid = null, pid = null;
        string? serial = null, location = null;

        // The Ports-class node is the CH340's own USB node, or an interface of a composite device, or a child of a
        // vendor bus driver (FTDIBUS\...). Walk up to the first USB node that is a whole device, not an interface:
        // that is where the serial number and the socket live.
        var node = devInst;
        for (var depth = 0; depth < 4; depth++)
        {
            if (UsbInstanceId.TryParse(DeviceId(node), out var usb))
            {
                vid ??= usb.VendorId;
                pid ??= usb.ProductId;
                if (!usb.IsInterface)
                {
                    serial = usb.SerialNumber;
                    location = LocationPath(node);
                    break;
                }
            }
            if (CM_Get_Parent(&node, node, 0) != CR_SUCCESS)
            {
                break;
            }
        }

        return new SerialPortInfo(portName)
        {
            Description = description,
            VendorId = vid,
            ProductId = pid,
            SerialNumber = serial,
            DeviceInstanceId = instanceId,
            LocationPath = location,
            Bluetooth = BluetoothInstanceId.TryParse(instanceId, out var bluetooth) ? DescribeBluetooth(bluetooth.Address) : null,
        };
    }

    /// <summary>
    /// The paired device at <paramref name="address"/> as Windows recorded it when it was paired (its name and Class of
    /// Device, under <c>BTHPORT\Parameters\Devices</c>), or only the address when the record cannot be read.
    /// </summary>
    private static SerialBluetoothDevice DescribeBluetooth(ulong address)
    {
        var device = new SerialBluetoothDevice(address);
        if (device.IsIncoming)
        {
            return device;
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{device.AddressText}");
            if (key is null)
            {
                return device;
            }
            var name = key.GetValue("Name") is byte[] { Length: > 0 } bytes ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0') : null;
            uint? cod = key.GetValue("COD") is int value ? unchecked((uint)value) : null;
            return device with { Name = name is { Length: > 0 } ? name : null, ClassOfDevice = cod };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return device;
        }
    }

    private static string? ReadPortName(nint set, SP_DEVINFO_DATA* data)
    {
        var hkey = SetupDiOpenDevRegKey(set, data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (hkey == InvalidHandle || hkey == 0)
        {
            return null;
        }
        // The key takes the handle over; disposing both is harmless (a SafeHandle releases once).
        using var handle = new SafeRegistryHandle(hkey, ownsHandle: true);
        using var key = RegistryKey.FromHandle(handle);
        return key.GetValue("PortName") as string is { Length: > 0 } name ? name : null;
    }

    private static string? ReadFriendlyName(nint set, SP_DEVINFO_DATA* data)
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* p = buffer)
        {
            uint type, required;
            if (SetupDiGetDeviceRegistryPropertyW(set, data, SPDRP_FRIENDLYNAME, &type, p, (uint)buffer.Length, &required) == 0)
            {
                return null;
            }
            return DeviceStrings.MultiSz(buffer[..(int)Math.Min(required, (uint)buffer.Length)]).FirstOrDefault();
        }
    }

    private static string? DeviceId(uint devInst)
    {
        const int MaxDeviceIdLen = 200;
        var buffer = stackalloc char[MaxDeviceIdLen + 1];
        return CM_Get_Device_IDW(devInst, buffer, MaxDeviceIdLen + 1, 0) == CR_SUCCESS ? new string(buffer) : null;
    }

    private static string? LocationPath(uint devInst)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 37 };
        uint type, size = 0;
        if (CM_Get_DevNode_PropertyW(devInst, &key, &type, null, &size, 0) != CR_BUFFER_SMALL || size == 0)
        {
            return null;
        }
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (CM_Get_DevNode_PropertyW(devInst, &key, &type, p, &size, 0) != CR_SUCCESS)
            {
                return null;
            }
        }
        // A string list; the first entry is the PCI path, a later one may be an ACPI path.
        return DeviceStrings.MultiSz(buffer).FirstOrDefault();
    }

    private const uint DIGCF_PRESENT = 0x2;
    private const uint DICS_FLAG_GLOBAL = 0x1;
    private const uint DIREG_DEV = 0x1;
    private const int KEY_READ = 0x20019;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint CR_SUCCESS = 0x0;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiGetClassDevsW(Guid* classGuid, char* enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial int SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, SP_DEVINFO_DATA* deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial int SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiOpenDevRegKey(nint deviceInfoSet, SP_DEVINFO_DATA* deviceInfoData, uint scope, uint hwProfile, uint keyType, int samDesired);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial int SetupDiGetDeviceRegistryPropertyW(nint deviceInfoSet, SP_DEVINFO_DATA* deviceInfoData, uint property,
        uint* propertyRegDataType, byte* propertyBuffer, uint propertyBufferSize, uint* requiredSize);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(uint* parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_IDW(uint devInst, char* buffer, uint bufferLen, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, DEVPROPKEY* propertyKey, uint* propertyType, byte* propertyBuffer, uint* propertyBufferSize, uint flags);
}
