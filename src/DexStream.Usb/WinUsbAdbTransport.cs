using System.Buffers;
using System.Runtime.InteropServices;
using DexStream.Core.Adb;
using DexStream.Usb.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace DexStream.Usb;

/// <summary>
/// An <see cref="IAdbTransport"/> that talks to the device's ADB interface directly over WinUSB.
/// </summary>
/// <remarks>
/// <para>
/// The ADB interface is a vendor-specific USB interface with class <c>0xFF</c>, subclass
/// <c>0x42</c> and protocol <c>0x01</c>, exposing one bulk IN and one bulk OUT endpoint. Everything
/// ADB does rides those two pipes, so implementing them here removes any dependency on
/// <c>adb.exe</c> and keeps the whole data path inside this process.
/// </para>
/// <para>
/// Only one process may hold the interface at a time. If the ADB server is running it will already
/// have claimed it, and opening fails with a sharing violation; the error message says so and names
/// the fix.
/// </para>
/// </remarks>
public sealed class WinUsbAdbTransport : IAdbTransport
{
    /// <summary>USB interface class of an ADB interface.</summary>
    public const byte AdbInterfaceClass = 0xFF;

    /// <summary>USB interface subclass of an ADB interface.</summary>
    public const byte AdbInterfaceSubClass = 0x42;

    /// <summary>USB interface protocol of an ADB interface.</summary>
    public const byte AdbInterfaceProtocol = 0x01;

    /// <summary>Largest single WinUSB transfer we issue. Larger transfers gain nothing and add latency.</summary>
    public const int MaxTransfer = 256 * 1024;

    private readonly SafeFileHandle _deviceHandle;
    private readonly IntPtr _interfaceHandle;
    private readonly byte _readPipeId;
    private readonly byte _writePipeId;
    private readonly SerialWorkQueue _readQueue;
    private readonly SerialWorkQueue _writeQueue;
    private readonly ILogger _logger;

    private bool _disposed;

    private WinUsbAdbTransport(
        UsbDeviceInfo device,
        SafeFileHandle deviceHandle,
        IntPtr interfaceHandle,
        byte readPipeId,
        byte writePipeId,
        ushort maxPacketSize,
        ILogger logger)
    {
        Device = device;
        _deviceHandle = deviceHandle;
        _interfaceHandle = interfaceHandle;
        _readPipeId = readPipeId;
        _writePipeId = writePipeId;
        MaxPacketSize = maxPacketSize;
        _logger = logger;
        _readQueue = new SerialWorkQueue("DexStream USB read");
        _writeQueue = new SerialWorkQueue("DexStream USB write");
        IsConnected = true;
    }

    /// <summary>The device this transport is bound to.</summary>
    public UsbDeviceInfo Device { get; }

    /// <summary>The bulk endpoint's maximum packet size, 512 at high speed and 1024 at SuperSpeed.</summary>
    public ushort MaxPacketSize { get; }

    public string Description => Device.ToString();

    public int MaxTransferSize => MaxTransfer;

    public bool IsConnected { get; private set; }

    /// <summary>
    /// Opens the ADB interface on <paramref name="device"/>.
    /// </summary>
    /// <exception cref="UsbDeviceException">
    /// The interface could not be opened or is not an ADB interface. The message distinguishes the
    /// common causes: the ADB server holding the handle, a missing driver, and a device that has
    /// USB debugging switched off so no ADB interface is published at all.
    /// </exception>
    public static WinUsbAdbTransport Open(UsbDeviceInfo device, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        SafeFileHandle handle = Kernel32.CreateFile(
            device.DevicePath,
            Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new UsbDeviceException(DescribeOpenFailure(device, error), error);
        }

        if (!WinUsb.WinUsb_Initialize(handle, out IntPtr interfaceHandle))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new UsbDeviceException(
                $"WinUsb_Initialize failed for {device.Description} (error {error}). The interface is " +
                "bound to a driver other than WinUSB. See docs/DRIVERS.md for how to install the " +
                "Samsung or Google USB driver.",
                error);
        }

        try
        {
            if (!WinUsb.WinUsb_QueryInterfaceSettings(interfaceHandle, 0, out USB_INTERFACE_DESCRIPTOR descriptor))
            {
                int error = Marshal.GetLastWin32Error();
                throw new UsbDeviceException(
                    $"Could not read the USB interface descriptor for {device.Description} (error {error}).",
                    error);
            }

            if (descriptor.BInterfaceClass != AdbInterfaceClass
                || descriptor.BInterfaceSubClass != AdbInterfaceSubClass
                || descriptor.BInterfaceProtocol != AdbInterfaceProtocol)
            {
                throw new UsbDeviceException(
                    $"{device.Description} is not an ADB interface " +
                    $"(class 0x{descriptor.BInterfaceClass:X2}, subclass 0x{descriptor.BInterfaceSubClass:X2}, " +
                    $"protocol 0x{descriptor.BInterfaceProtocol:X2}; expected 0xFF/0x42/0x01). " +
                    "Enable 'USB debugging' in Developer options and set the USB connection mode to " +
                    "transferring files.");
            }

            (byte readPipe, byte writePipe, ushort maxPacket) = FindBulkPipes(interfaceHandle, descriptor, device);
            ConfigurePipes(interfaceHandle, readPipe, writePipe);

            logger.LogInformation(
                "Opened ADB interface on {Device}: bulk IN 0x{ReadPipe:X2}, bulk OUT 0x{WritePipe:X2}, " +
                "max packet {MaxPacket} bytes.",
                device,
                readPipe,
                writePipe,
                maxPacket);

            return new WinUsbAdbTransport(
                device, handle, interfaceHandle, readPipe, writePipe, maxPacket, logger);
        }
        catch
        {
            WinUsb.WinUsb_Free(interfaceHandle);
            handle.Dispose();
            throw;
        }
    }

    private static string DescribeOpenFailure(UsbDeviceInfo device, int error) => error switch
    {
        Kernel32.ERROR_SHARING_VIOLATION or Kernel32.ERROR_ACCESS_DENIED =>
            $"Another process already owns the ADB interface on {device.Description}. This is almost " +
            "always the ADB server: run 'adb kill-server' (or close Android Studio and Samsung " +
            $"Smart Switch) and try again. Win32 error {error}.",
        Kernel32.ERROR_FILE_NOT_FOUND =>
            $"The USB device disappeared before it could be opened ({device.Description}).",
        _ => $"Could not open {device.Description}: Win32 error {error}.",
    };

    /// <summary>Locates the interface's bulk IN and bulk OUT endpoints.</summary>
    private static (byte Read, byte Write, ushort MaxPacket) FindBulkPipes(
        IntPtr interfaceHandle,
        USB_INTERFACE_DESCRIPTOR descriptor,
        UsbDeviceInfo device)
    {
        byte? read = null;
        byte? write = null;
        ushort maxPacket = 512;

        for (byte index = 0; index < descriptor.BNumEndpoints; index++)
        {
            if (!WinUsb.WinUsb_QueryPipe(interfaceHandle, 0, index, out WINUSB_PIPE_INFORMATION pipe))
            {
                continue;
            }

            if (pipe.PipeType != UsbdPipeType.Bulk)
            {
                continue;
            }

            if (WinUsb.IsInputEndpoint(pipe.PipeId))
            {
                read ??= pipe.PipeId;
            }
            else
            {
                write ??= pipe.PipeId;
            }

            if (pipe.MaximumPacketSize > 0)
            {
                maxPacket = pipe.MaximumPacketSize;
            }
        }

        if (read is null || write is null)
        {
            throw new UsbDeviceException(
                $"{device.Description} does not expose the pair of bulk endpoints ADB needs " +
                $"(found IN={read?.ToString("X2") ?? "none"}, OUT={write?.ToString("X2") ?? "none"}).");
        }

        return (read.Value, write.Value, maxPacket);
    }

    /// <summary>
    /// Applies the pipe policies that give the read pipe stream semantics.
    /// </summary>
    /// <remarks>
    /// <c>ALLOW_PARTIAL_READS</c> with <c>AUTO_FLUSH</c> off is the combination that makes a bulk IN
    /// pipe behave like a byte stream: a read may return less than requested, and anything the device
    /// sent beyond the requested length is kept for the next read instead of being discarded. Without
    /// it, a read shorter than the arriving packet would silently lose bytes and desynchronise the
    /// ADB framing.
    /// </remarks>
    private static void ConfigurePipes(IntPtr interfaceHandle, byte readPipe, byte writePipe)
    {
        WinUsb.SetBoolPolicy(interfaceHandle, readPipe, WinUsb.RAW_IO, false);
        WinUsb.SetBoolPolicy(interfaceHandle, readPipe, WinUsb.ALLOW_PARTIAL_READS, true);
        WinUsb.SetBoolPolicy(interfaceHandle, readPipe, WinUsb.AUTO_FLUSH, false);
        WinUsb.SetBoolPolicy(interfaceHandle, readPipe, WinUsb.IGNORE_SHORT_PACKETS, false);
        WinUsb.SetBoolPolicy(interfaceHandle, readPipe, WinUsb.AUTO_CLEAR_STALL, true);

        // No read timeout: an idle ADB connection is legitimately silent, so a timeout would fire
        // constantly. Cancellation goes through WinUsb_AbortPipe instead, and the device agent sends
        // heartbeats so that genuine silence is still detectable.
        WinUsb.SetUInt32Policy(interfaceHandle, readPipe, WinUsb.PIPE_TRANSFER_TIMEOUT, 0);

        WinUsb.SetBoolPolicy(interfaceHandle, writePipe, WinUsb.RAW_IO, false);
        WinUsb.SetBoolPolicy(interfaceHandle, writePipe, WinUsb.SHORT_PACKET_TERMINATE, false);
        WinUsb.SetBoolPolicy(interfaceHandle, writePipe, WinUsb.AUTO_CLEAR_STALL, true);

        // A write that cannot complete in 5s means the device has stopped reading; failing is better
        // than blocking the writer thread indefinitely.
        WinUsb.SetUInt32Policy(interfaceHandle, writePipe, WinUsb.PIPE_TRANSFER_TIMEOUT, 5000);
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return;
        }

        using CancellationTokenRegistration registration = RegisterAbort(_readPipeId, cancellationToken);
        await _readQueue.RunAsync(() => ReadExactBlocking(buffer), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return;
        }

        using CancellationTokenRegistration registration = RegisterAbort(_writePipeId, cancellationToken);
        await _writeQueue.RunAsync(() => WriteAllBlocking(buffer), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Arranges for a blocked USB transfer to be released when the token fires. WinUSB completes the
    /// pending transfer with ERROR_OPERATION_ABORTED, which the blocking helpers translate into
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    private CancellationTokenRegistration RegisterAbort(byte pipeId, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => WinUsb.WinUsb_AbortPipe(_interfaceHandle, pipeId))
            : default;

    private unsafe bool ReadExactBlocking(Memory<byte> buffer)
    {
        int offset = 0;
        using MemoryHandle pin = buffer.Pin();
        byte* basePointer = (byte*)pin.Pointer;

        while (offset < buffer.Length)
        {
            uint request = (uint)Math.Min(MaxTransfer, buffer.Length - offset);

            if (!WinUsb.WinUsb_ReadPipe(
                    _interfaceHandle, _readPipeId, basePointer + offset, request, out uint transferred, IntPtr.Zero))
            {
                throw TranslateError(Marshal.GetLastWin32Error(), "read from");
            }

            if (transferred == 0)
            {
                // A bulk IN that completes with nothing is a zero-length packet. adbd sends one only
                // as a transfer terminator, so treat it as a no-op and read again rather than
                // spinning on a closed pipe.
                continue;
            }

            offset += (int)transferred;
        }

        return true;
    }

    private unsafe bool WriteAllBlocking(ReadOnlyMemory<byte> buffer)
    {
        int offset = 0;
        using MemoryHandle pin = buffer.Pin();
        byte* basePointer = (byte*)pin.Pointer;

        while (offset < buffer.Length)
        {
            uint request = (uint)Math.Min(MaxTransfer, buffer.Length - offset);

            if (!WinUsb.WinUsb_WritePipe(
                    _interfaceHandle, _writePipeId, basePointer + offset, request, out uint transferred, IntPtr.Zero))
            {
                throw TranslateError(Marshal.GetLastWin32Error(), "write to");
            }

            if (transferred == 0)
            {
                throw new UsbDeviceException(
                    $"The USB write pipe accepted no data on {Device.Description}; the device has " +
                    "stopped reading.");
            }

            offset += (int)transferred;
        }

        return true;
    }

    private Exception TranslateError(int error, string verb)
    {
        switch (error)
        {
            case Kernel32.ERROR_OPERATION_ABORTED:
                return new OperationCanceledException($"USB {verb} was cancelled.");

            case Kernel32.ERROR_DEVICE_NOT_CONNECTED:
            case Kernel32.ERROR_NO_SUCH_DEVICE:
            case Kernel32.ERROR_GEN_FAILURE:
            case Kernel32.ERROR_FILE_NOT_FOUND:
                IsConnected = false;
                return new UsbDeviceException(
                    $"The device was disconnected while trying to {verb} it ({Device.Description}). " +
                    $"Win32 error {error}.",
                    error);

            case Kernel32.ERROR_SEM_TIMEOUT:
                return new UsbDeviceException(
                    $"A USB {verb} operation timed out on {Device.Description}. " +
                    "Try a different cable or port: a marginal USB 2.0 cable often shows up this way.",
                    error);

            default:
                IsConnected = false;
                return new UsbDeviceException(
                    $"USB {verb} failed on {Device.Description} with Win32 error {error}.",
                    error);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        IsConnected = false;

        // Abort first so any blocked transfer returns and the worker threads can exit.
        WinUsb.WinUsb_AbortPipe(_interfaceHandle, _readPipeId);
        WinUsb.WinUsb_AbortPipe(_interfaceHandle, _writePipeId);

        _readQueue.Dispose();
        _writeQueue.Dispose();

        WinUsb.WinUsb_Free(_interfaceHandle);
        _deviceHandle.Dispose();

        _logger.LogDebug("Closed ADB interface on {Device}.", Device);
        return ValueTask.CompletedTask;
    }
}
