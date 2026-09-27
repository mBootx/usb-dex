using System.Buffers.Binary;

namespace DexStream.Core.Protocol;

/// <summary>
/// The 16-byte header in front of every video-channel payload.
/// </summary>
/// <remarks>
/// Layout, big-endian:
/// <code>
/// u8 type   u8 flags   u16 reserved
/// u32 payloadLength
/// u64 presentationTimeUs   (device monotonic clock, microseconds)
/// </code>
/// </remarks>
public readonly record struct DexPacketHeader(
    DexPacketType Type,
    DexPacketFlags Flags,
    int PayloadLength,
    ulong PresentationTimeUs)
{
    public bool IsKeyFrame => Flags.HasFlag(DexPacketFlags.KeyFrame);

    public bool IsCodecConfig => Type == DexPacketType.Config || Flags.HasFlag(DexPacketFlags.CodecConfig);

    public void Write(Span<byte> destination)
    {
        if (destination.Length < DexProtocol.PacketHeaderSize)
        {
            throw new ArgumentException(
                $"Packet header needs {DexProtocol.PacketHeaderSize} bytes.",
                nameof(destination));
        }

        destination[0] = (byte)Type;
        destination[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], 0);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..8], PayloadLength);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], PresentationTimeUs);
    }

    /// <summary>Parses a packet header and validates the payload length.</summary>
    /// <exception cref="DexProtocolException">The payload length is negative or absurdly large.</exception>
    public static DexPacketHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < DexProtocol.PacketHeaderSize)
        {
            throw new ArgumentException(
                $"Packet header needs {DexProtocol.PacketHeaderSize} bytes, got {source.Length}.",
                nameof(source));
        }

        var type = (DexPacketType)source[0];
        var flags = (DexPacketFlags)source[1];
        int length = BinaryPrimitives.ReadInt32BigEndian(source[4..8]);
        ulong pts = BinaryPrimitives.ReadUInt64BigEndian(source[8..16]);

        if (length < 0 || length > DexProtocol.MaxPacketSize)
        {
            throw new DexProtocolException(
                $"The agent announced a {length}-byte packet, which is outside the accepted range " +
                $"of 0..{DexProtocol.MaxPacketSize}. The stream is out of sync.");
        }

        return new DexPacketHeader(type, flags, length, pts);
    }
}
