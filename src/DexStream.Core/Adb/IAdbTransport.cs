namespace DexStream.Core.Adb;

/// <summary>
/// A raw bidirectional byte pipe to adbd. The USB implementation writes to and reads from the
/// bulk endpoints of the device's ADB interface; tests substitute an in-memory pipe.
/// </summary>
/// <remarks>
/// Reads are expected to satisfy the requested length exactly, which matches how adbd frames
/// its writes (one transfer for the 24-byte header, one for the payload) and how <c>adb</c>'s
/// own Windows backend reads them.
/// </remarks>
public interface IAdbTransport : IAsyncDisposable
{
    /// <summary>Human-readable identification used in log messages and the UI.</summary>
    string Description { get; }

    /// <summary>
    /// Largest single transfer the transport will accept. Callers must chunk payloads larger
    /// than this value.
    /// </summary>
    int MaxTransferSize { get; }

    /// <summary>True while the underlying handle is usable.</summary>
    bool IsConnected { get; }

    /// <summary>Writes the whole buffer, chunking internally if required.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Fills <paramref name="buffer"/> completely.
    /// </summary>
    /// <exception cref="EndOfStreamException">The device closed the pipe.</exception>
    ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
