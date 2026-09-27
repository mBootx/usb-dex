using SharpGen.Runtime;

namespace DexStream.Media;

/// <summary>
/// The Media Foundation result codes the decode loop has to react to rather than treat as failures.
/// </summary>
/// <remarks>
/// These three are ordinary control flow for a transform: the decoder wants more data before it can
/// produce a frame, it has changed its output format and needs the type renegotiated, or it cannot
/// take more input until its pending output is drained. Treating any of them as an error would break
/// the stream on the first frame.
/// </remarks>
internal static class MediaFoundationHResults
{
    /// <summary><c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>: no output is available yet.</summary>
    public static readonly Result NeedMoreInput = new(unchecked((int)0xC00D6D72));

    /// <summary><c>MF_E_TRANSFORM_STREAM_CHANGE</c>: the output type must be renegotiated.</summary>
    public static readonly Result StreamChange = new(unchecked((int)0xC00D6D61));

    /// <summary><c>MF_E_NOTACCEPTING</c>: drain the output before submitting more input.</summary>
    public static readonly Result NotAccepting = new(unchecked((int)0xC00D36B5));

    /// <summary><c>MF_E_INVALIDMEDIATYPE</c>: the type offered was rejected.</summary>
    public static readonly Result InvalidMediaType = new(unchecked((int)0xC00D36B4));

    /// <summary><c>MF_E_NO_MORE_TYPES</c>: the transform has listed every type it supports.</summary>
    public static readonly Result NoMoreTypes = new(unchecked((int)0xC00D36B9));
}
