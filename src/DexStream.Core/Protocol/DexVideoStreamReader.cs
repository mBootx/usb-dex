using System.Buffers;

namespace DexStream.Core.Protocol;

/// <summary>A packet read from the video channel. The payload is valid until the next read.</summary>
public readonly record struct DexPacket(DexPacketHeader Header, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads the DexStream video channel: the fixed stream header followed by an endless sequence of
/// packets. Payload buffers are pooled and reused, so callers must copy anything they keep.
/// </summary>
public sealed class DexVideoStreamReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[DexProtocol.PacketHeaderSize];
    private byte[] _payloadBuffer;
    private bool _disposed;

    public DexVideoStreamReader(Stream stream, int initialBufferSize = 512 * 1024)
    {
        _stream = stream;
        _payloadBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialBufferSize, DexProtocol.PacketHeaderSize));
    }

    /// <summary>The stream header, available once <see cref="ReadStreamHeaderAsync"/> has completed.</summary>
    public DexStreamHeader? StreamHeader { get; private set; }

    /// <summary>Reads and validates the leading 92-byte stream header.</summary>
    public async ValueTask<DexStreamHeader> ReadStreamHeaderAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];
        await ReadExactAsync(buffer, cancellationToken).ConfigureAwait(false);
        DexStreamHeader header = DexStreamHeader.Parse(buffer);
        StreamHeader = header;
        return header;
    }

    /// <summary>
    /// Reads the next packet, or null at clean end of stream.
    /// </summary>
    /// <remarks>
    /// The returned payload points into a pooled buffer owned by this reader and is overwritten by
    /// the following call to <see cref="ReadPacketAsync"/>.
    /// </remarks>
    public async ValueTask<DexPacket?> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await TryReadExactAsync(_headerBuffer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DexPacketHeader header = DexPacketHeader.Parse(_headerBuffer);

        if (header.PayloadLength > _payloadBuffer.Length)
        {
            ArrayPool<byte>.Shared.Return(_payloadBuffer);
            _payloadBuffer = ArrayPool<byte>.Shared.Rent(header.PayloadLength);
        }

        Memory<byte> payload = _payloadBuffer.AsMemory(0, header.PayloadLength);
        if (header.PayloadLength > 0)
        {
            await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return new DexPacket(header, payload);
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await TryReadExactAsync(buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new DexProtocolException(
                $"The device agent closed the video channel while {buffer.Length} more bytes were expected.");
        }
    }

    /// <summary>Fills the buffer. Returns false only when nothing at all could be read.</summary>
    private async ValueTask<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new DexProtocolException(
                    $"The device agent closed the video channel after {offset} of {buffer.Length} bytes.");
            }

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_payloadBuffer);
    }
}
