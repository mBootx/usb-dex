namespace DexStream.Core.Adb;

/// <summary>Raised when the device violates the ADB wire protocol or rejects a request.</summary>
public class AdbProtocolException : IOException
{
    public AdbProtocolException(string message) : base(message)
    {
    }

    public AdbProtocolException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Raised when the device has not accepted this host's key. The user must tap
/// <em>Allow</em> on the "Allow USB debugging?" prompt on the phone.
/// </summary>
public sealed class AdbAuthorizationException : AdbProtocolException
{
    public AdbAuthorizationException(string message) : base(message)
    {
    }
}
