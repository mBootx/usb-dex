namespace DexStream.Core.Protocol;

/// <summary>Raised when the device agent's stream does not follow the DexStream wire protocol.</summary>
public sealed class DexProtocolException : IOException
{
    public DexProtocolException(string message) : base(message)
    {
    }

    public DexProtocolException(string message, Exception inner) : base(message, inner)
    {
    }
}
