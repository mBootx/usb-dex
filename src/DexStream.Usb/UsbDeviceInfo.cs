using DexStream.Core.Device;

namespace DexStream.Usb;

/// <summary>An ADB interface discovered on the USB bus.</summary>
/// <param name="DevicePath">The <c>\\?\usb#...</c> path to open with <c>CreateFile</c>.</param>
/// <param name="VendorId">USB vendor id parsed from the device path.</param>
/// <param name="ProductId">USB product id parsed from the device path.</param>
/// <param name="SerialNumber">
/// The device's USB serial number, which for Android devices is the same string
/// <c>adb devices</c> prints. Empty when the path carries no serial (composite MI_ instances).
/// </param>
/// <param name="Description">The driver's friendly name, for example <c>SAMSUNG Android ADB Interface</c>.</param>
public sealed record UsbDeviceInfo(
    string DevicePath,
    int VendorId,
    int ProductId,
    string SerialNumber,
    string Description)
{
    public bool IsSamsung => VendorId == SamsungDeviceCatalog.SamsungVendorId;

    public override string ToString()
        => $"{Description} (VID_{VendorId:X4}&PID_{ProductId:X4}" +
           (SerialNumber.Length > 0 ? $", serial {SerialNumber}" : string.Empty) + ")";
}

/// <summary>Raised when a USB device cannot be opened or claimed.</summary>
public sealed class UsbDeviceException : IOException
{
    public UsbDeviceException(string message) : base(message)
    {
    }

    public UsbDeviceException(string message, int win32Error)
        : base(message) => Win32Error = win32Error;

    public int Win32Error { get; }
}
