using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DexStream.Usb.Native;

/// <summary>USB endpoint transfer types, as reported by <c>WinUsb_QueryPipe</c>.</summary>
internal enum UsbdPipeType
{
    Control = 0,
    Isochronous = 1,
    Bulk = 2,
    Interrupt = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINUSB_PIPE_INFORMATION
{
    public UsbdPipeType PipeType;
    public byte PipeId;
    public ushort MaximumPacketSize;
    public byte Interval;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct USB_INTERFACE_DESCRIPTOR
{
    public byte BLength;
    public byte BDescriptorType;
    public byte BInterfaceNumber;
    public byte BAlternateSetting;
    public byte BNumEndpoints;
    public byte BInterfaceClass;
    public byte BInterfaceSubClass;
    public byte BInterfaceProtocol;
    public byte IInterface;
}

/// <summary>P/Invoke surface for WinUSB.</summary>
internal static partial class WinUsb
{
    // Pipe policy identifiers from winusbio.h.
    internal const uint SHORT_PACKET_TERMINATE = 0x01;
    internal const uint AUTO_CLEAR_STALL = 0x02;
    internal const uint PIPE_TRANSFER_TIMEOUT = 0x03;
    internal const uint IGNORE_SHORT_PACKETS = 0x04;
    internal const uint ALLOW_PARTIAL_READS = 0x05;
    internal const uint AUTO_FLUSH = 0x06;
    internal const uint RAW_IO = 0x07;

    internal const byte USB_ENDPOINT_DIRECTION_MASK = 0x80;

    /// <summary>True when the endpoint address marks a device-to-host pipe.</summary>
    internal static bool IsInputEndpoint(byte pipeId) => (pipeId & USB_ENDPOINT_DIRECTION_MASK) != 0;

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Free(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        out USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryPipe(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out WINUSB_PIPE_INFORMATION pipeInformation);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        void* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);

    /// <summary>Sets a <c>bool</c> pipe policy, which WinUSB reads as a single byte.</summary>
    internal static unsafe bool SetBoolPolicy(IntPtr handle, byte pipeId, uint policy, bool value)
    {
        byte raw = value ? (byte)1 : (byte)0;
        return WinUsb_SetPipePolicy(handle, pipeId, policy, sizeof(byte), &raw);
    }

    /// <summary>Sets a <c>uint</c> pipe policy such as <c>PIPE_TRANSFER_TIMEOUT</c>.</summary>
    internal static unsafe bool SetUInt32Policy(IntPtr handle, byte pipeId, uint policy, uint value)
        => WinUsb_SetPipePolicy(handle, pipeId, policy, sizeof(uint), &value);
}
