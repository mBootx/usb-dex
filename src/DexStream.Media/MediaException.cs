namespace DexStream.Media;

/// <summary>Raised when the Direct3D or Media Foundation pipeline cannot be built or run.</summary>
public sealed class MediaPipelineException : Exception
{
    public MediaPipelineException(string message) : base(message)
    {
    }

    public MediaPipelineException(string message, Exception inner) : base(message, inner)
    {
    }
}
