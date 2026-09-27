using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DexStream.Usb.Native;

internal static partial class Kernel32
{
    internal const uint GENERIC_READ = 0x8000_0000;
    internal const uint GENERIC_WRITE = 0x4000_0000;
    internal const uint FILE_SHARE_READ = 0x0000_0001;
    internal const uint FILE_SHARE_WRITE = 0x0000_0002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x0000_0080;

    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_OPERATION_ABORTED = 995;
    internal const int ERROR_SEM_TIMEOUT = 121;
    internal const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    internal const int ERROR_NO_SUCH_DEVICE = 433;
    internal const int ERROR_GEN_FAILURE = 31;
    internal const int ERROR_BAD_COMMAND = 22;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
