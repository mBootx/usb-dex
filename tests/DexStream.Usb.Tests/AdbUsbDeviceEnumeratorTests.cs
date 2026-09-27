using DexStream.Usb;
using Xunit;

namespace DexStream.Usb.Tests;

public class AdbUsbDeviceEnumeratorTests
{
    [Fact]
    public void ParseDevicePath_ReadsVendorAndProductIds()
    {
        (int vendorId, int productId, _) = AdbUsbDeviceEnumerator.ParseDevicePath(
            @"\\?\usb#vid_04e8&pid_6860&mi_01#7&1a2b3c4d&0&0001#{f72fe0d4-cbcb-407d-8814-9ed673d0dd6b}");

        Assert.Equal(0x04E8, vendorId);
        Assert.Equal(0x6860, productId);
    }

    [Fact]
    public void ParseDevicePath_ReadsTheSerialWhenTheInstanceIdIsOne()
    {
        (_, _, string serial) = AdbUsbDeviceEnumerator.ParseDevicePath(
            @"\\?\usb#vid_04e8&pid_6860#R5CN30ABCDE#{f72fe0d4-cbcb-407d-8814-9ed673d0dd6b}");

        Assert.Equal("R5CN30ABCDE", serial);
    }

    [Fact]
    public void ParseDevicePath_ReturnsNoSerialForAWindowsGeneratedInstanceId()
    {
        // "7&1a2b3c4d&0&0001" is assigned by Windows, not reported by the device, so it must not be
        // presented to the user as a serial number.
        (_, _, string serial) = AdbUsbDeviceEnumerator.ParseDevicePath(
            @"\\?\usb#vid_04e8&pid_6860&mi_01#7&1a2b3c4d&0&0001#{f72fe0d4-cbcb-407d-8814-9ed673d0dd6b}");

        Assert.Equal(string.Empty, serial);
    }

    [Fact]
    public void ParseDevicePath_IsCaseInsensitive()
    {
        (int vendorId, int productId, _) = AdbUsbDeviceEnumerator.ParseDevicePath(
            @"\\?\USB#VID_04E8&PID_6866#SERIAL1#{GUID}");

        Assert.Equal(0x04E8, vendorId);
        Assert.Equal(0x6866, productId);
    }

    [Fact]
    public void ParseDevicePath_ReturnsZeroWhenTheFieldsAreAbsent()
    {
        (int vendorId, int productId, string serial) =
            AdbUsbDeviceEnumerator.ParseDevicePath(@"\\?\something-else");

        Assert.Equal(0, vendorId);
        Assert.Equal(0, productId);
        Assert.Equal(string.Empty, serial);
    }

    [Fact]
    public void AndroidAdbInterfaceGuid_MatchesTheAndroidWinUsbInf()
        => Assert.Equal(
            new Guid("f72fe0d4-cbcb-407d-8814-9ed673d0dd6b"),
            AdbUsbDeviceEnumerator.AndroidAdbInterfaceGuid);

    [Fact]
    public void Enumerate_DoesNotThrowWhenNoDeviceIsPresent()
    {
        // The CI runner has no phone attached, so this must return an empty list rather than fail.
        IReadOnlyList<UsbDeviceInfo> devices = new AdbUsbDeviceEnumerator().Enumerate(samsungOnly: true);

        Assert.NotNull(devices);
    }

    [Fact]
    public void UsbDeviceInfo_DescribesItselfWithHexIds()
    {
        var device = new UsbDeviceInfo(@"\\?\usb#x", 0x04E8, 0x6860, "R5CN30ABCDE", "SAMSUNG Android ADB Interface");

        string text = device.ToString();

        Assert.Contains("VID_04E8", text);
        Assert.Contains("PID_6860", text);
        Assert.Contains("R5CN30ABCDE", text);
        Assert.True(device.IsSamsung);
    }

    [Fact]
    public void UsbDeviceInfo_OmitsAnEmptySerial()
    {
        var device = new UsbDeviceInfo(@"\\?\usb#x", 0x04E8, 0x6860, string.Empty, "ADB");

        Assert.DoesNotContain("serial", device.ToString());
    }

    [Fact]
    public void AdbInterfaceConstants_MatchTheUsbDescriptorValues()
    {
        Assert.Equal(0xFF, (int)WinUsbAdbTransport.AdbInterfaceClass);
        Assert.Equal(0x42, (int)WinUsbAdbTransport.AdbInterfaceSubClass);
        Assert.Equal(0x01, (int)WinUsbAdbTransport.AdbInterfaceProtocol);
    }
}
