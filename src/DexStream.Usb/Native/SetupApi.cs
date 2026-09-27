using System.Runtime.InteropServices;

namespace DexStream.Usb.Native;

/// <summary>P/Invoke surface for the SetupAPI device-interface enumeration calls.</summary>
internal static partial class SetupApi
{
    internal const int DIGCF_PRESENT = 0x0000_0002;
    internal const int DIGCF_DEVICEINTERFACE = 0x0000_0010;

    internal const int SPDRP_DEVICEDESC = 0x0000_0000;
    internal const int SPDRP_FRIENDLYNAME = 0x0000_000C;

    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int CbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SetupDiGetClassDevs(
        in Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        int flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        in Guid interfaceClassGuid,
        int memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    /// <summary>
    /// Reads the device interface detail. The native structure has a fixed <c>cbSize</c> followed by
    /// a variable-length path, so the buffer is handled as raw bytes rather than a marshalled struct.
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        int property,
        out int propertyRegDataType,
        byte[]? propertyBuffer,
        int propertyBufferSize,
        out int requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
