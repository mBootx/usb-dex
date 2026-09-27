using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DexStream.Core.Device;
using DexStream.Usb.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.Usb;

/// <summary>
/// Finds Android ADB interfaces on the USB bus using SetupAPI.
/// </summary>
/// <remarks>
/// <para>
/// Android's WinUSB INF (<c>android_winusb.inf</c>, which the Samsung and Google USB drivers both
/// derive from) publishes every ADB interface under one device interface class GUID. Enumerating
/// that GUID is therefore how a host finds candidate devices without walking every USB node.
/// </para>
/// <para>
/// The USB product id does not identify the handset: Samsung reuses a handful of product ids to
/// describe the active USB configuration (MTP, MTP+ADB, RNDIS and so on). The model is read from
/// <c>ro.product.model</c> after ADB is up, so this class only reports what the bus can tell us.
/// </para>
/// </remarks>
public sealed class AdbUsbDeviceEnumerator
{
    /// <summary>
    /// The device interface class GUID that <c>android_winusb.inf</c> assigns to ADB interfaces.
    /// </summary>
    public static readonly Guid AndroidAdbInterfaceGuid = new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");

    /// <summary>WinUSB's generic device interface GUID, used as a fallback for custom INFs.</summary>
    public static readonly Guid WinUsbDeviceInterfaceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private readonly ILogger _logger;

    public AdbUsbDeviceEnumerator(ILogger<AdbUsbDeviceEnumerator>? logger = null)
        => _logger = logger ?? NullLogger<AdbUsbDeviceEnumerator>.Instance;

    /// <summary>
    /// Lists every present ADB interface.
    /// </summary>
    /// <param name="samsungOnly">
    /// When true, non-Samsung vendor ids are filtered out. DeX is a Samsung feature, but display
    /// capture works on any device, so the UI leaves this off and reports support separately.
    /// </param>
    public IReadOnlyList<UsbDeviceInfo> Enumerate(bool samsungOnly = false)
    {
        var results = new List<UsbDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Guid guid in new[] { AndroidAdbInterfaceGuid, WinUsbDeviceInterfaceGuid })
        {
            foreach (UsbDeviceInfo device in EnumerateInterfaceClass(guid))
            {
                if (samsungOnly && !device.IsSamsung)
                {
                    continue;
                }

                if (seen.Add(device.DevicePath))
                {
                    results.Add(device);
                }
            }
        }

        return results;
    }

    private IEnumerable<UsbDeviceInfo> EnumerateInterfaceClass(Guid interfaceGuid)
    {
        IntPtr deviceInfoSet = SetupApi.SetupDiGetClassDevs(
            in interfaceGuid,
            enumerator: null,
            hwndParent: IntPtr.Zero,
            SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            _logger.LogDebug(
                "SetupDiGetClassDevs found no devices for interface class {Guid} (error {Error}).",
                interfaceGuid,
                Marshal.GetLastWin32Error());
            yield break;
        }

        try
        {
            var interfaceData = new SetupApi.SP_DEVICE_INTERFACE_DATA
            {
                CbSize = Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>(),
            };

            for (int index = 0; ; index++)
            {
                if (!SetupApi.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet, IntPtr.Zero, in interfaceGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != SetupApi.ERROR_NO_MORE_ITEMS)
                    {
                        _logger.LogDebug("SetupDiEnumDeviceInterfaces stopped with error {Error}.", error);
                    }

                    break;
                }

                UsbDeviceInfo? device = ReadInterfaceDetail(deviceInfoSet, ref interfaceData);
                if (device is not null)
                {
                    yield return device;
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private UsbDeviceInfo? ReadInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SetupApi.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        var deviceInfoData = new SetupApi.SP_DEVINFO_DATA
        {
            CbSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>(),
        };

        // First call with a null buffer to learn the required size.
        SetupApi.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, ref deviceInfoData);

        if (requiredSize <= 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W begins with cbSize, then a WCHAR[] path. cbSize must
            // be the size of the *fixed* part: 4 bytes of DWORD plus the alignment of the WCHAR
            // array, which is 8 on x64 and 6 on x86.
            int fixedSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buffer, fixedSize);

            if (!SetupApi.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref deviceInfoData))
            {
                _logger.LogDebug(
                    "SetupDiGetDeviceInterfaceDetail failed with error {Error}.",
                    Marshal.GetLastWin32Error());
                return null;
            }

            string? devicePath = Marshal.PtrToStringUni(buffer + 4);
            if (string.IsNullOrEmpty(devicePath))
            {
                return null;
            }

            (int vendorId, int productId, string serial) = ParseDevicePath(devicePath);
            string description = ReadDescription(deviceInfoSet, ref deviceInfoData) ?? "USB device";

            return new UsbDeviceInfo(devicePath, vendorId, productId, serial, description);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadDescription(IntPtr deviceInfoSet, ref SetupApi.SP_DEVINFO_DATA deviceInfoData)
    {
        foreach (int property in new[] { SetupApi.SPDRP_FRIENDLYNAME, SetupApi.SPDRP_DEVICEDESC })
        {
            byte[] buffer = new byte[512];
            if (SetupApi.SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet, ref deviceInfoData, property, out _, buffer, buffer.Length, out int size)
                && size > 0)
            {
                string value = Encoding.Unicode.GetString(buffer, 0, Math.Min(size, buffer.Length)).TrimEnd('\0');
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the vendor id, product id and serial from a device interface path.
    /// </summary>
    /// <remarks>
    /// Paths look like
    /// <c>\\?\usb#vid_04e8&amp;pid_6860&amp;mi_01#7&amp;1234abcd&amp;0&amp;0001#{guid}</c> for a
    /// composite interface, or
    /// <c>\\?\usb#vid_04e8&amp;pid_6860#R5CN30ABCDE#{guid}</c> when the instance id is the device
    /// serial. Only the second form carries a usable serial number, so the parser returns an empty
    /// string rather than a synthetic value for the first.
    /// </remarks>
    internal static (int VendorId, int ProductId, string Serial) ParseDevicePath(string devicePath)
    {
        int vendorId = ParseHexField(devicePath, "vid_");
        int productId = ParseHexField(devicePath, "pid_");

        string serial = string.Empty;
        string[] segments = devicePath.Split('#');
        if (segments.Length >= 3)
        {
            string candidate = segments[2];
            // A Windows-generated instance id looks like "7&1a2b3c4d&0&0001"; a real USB serial
            // contains no ampersands.
            if (candidate.Length > 0 && !candidate.Contains('&', StringComparison.Ordinal))
            {
                serial = candidate;
            }
        }

        return (vendorId, productId, serial);
    }

    private static int ParseHexField(string devicePath, string prefix)
    {
        int start = devicePath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return 0;
        }

        start += prefix.Length;
        int end = start;
        while (end < devicePath.Length && Uri.IsHexDigit(devicePath[end]))
        {
            end++;
        }

        return int.TryParse(
            devicePath.AsSpan(start, end - start),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : 0;
    }
}
