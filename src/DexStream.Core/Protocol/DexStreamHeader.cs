using System.Buffers.Binary;
using System.Text;

namespace DexStream.Core.Protocol;

/// <summary>
/// The fixed 92-byte header the agent sends when the video channel opens, and again inside a
/// <see cref="DexPacketType.DisplayChanged"/> packet when the capture geometry changes.
/// </summary>
/// <remarks>
/// Layout, big-endian throughout (Java's <c>DataOutputStream</c> default, so the agent needs no
/// byte swapping):
/// <code>
/// u32 magic ("DEXS")   u16 version   u16 flags
/// u8[64] deviceName (UTF-8, NUL padded)
/// u32 codec (fourcc)   u32 width   u32 height
/// u32 refreshRateMilliHz   u32 displayId
/// </code>
/// </remarks>
public readonly record struct DexStreamHeader(
    ushort Version,
    ushort Flags,
    string DeviceName,
    DexCodec Codec,
    int Width,
    int Height,
    int RefreshRateMilliHz,
    int DisplayId)
{
    /// <summary>Refresh rate in Hz.</summary>
    public double RefreshRateHz => RefreshRateMilliHz / 1000.0;

    /// <summary>Writes the header into <paramref name="destination"/>, which must hold 92 bytes.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < DexProtocol.StreamHeaderSize)
        {
            throw new ArgumentException(
                $"Stream header needs {DexProtocol.StreamHeaderSize} bytes.",
                nameof(destination));
        }

        destination[..DexProtocol.StreamHeaderSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], DexProtocol.VideoMagic);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], Flags);

        Span<byte> nameField = destination.Slice(8, DexProtocol.DeviceNameSize);
        WriteTruncatedUtf8(DeviceName, nameField);

        int offset = 8 + DexProtocol.DeviceNameSize;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), (uint)Codec);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset + 4, 4), Width);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset + 8, 4), Height);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset + 12, 4), RefreshRateMilliHz);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset + 16, 4), DisplayId);
    }

    /// <summary>Parses a 92-byte header.</summary>
    /// <exception cref="DexProtocolException">The magic, version or geometry is invalid.</exception>
    public static DexStreamHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < DexProtocol.StreamHeaderSize)
        {
            throw new ArgumentException(
                $"Stream header needs {DexProtocol.StreamHeaderSize} bytes, got {source.Length}.",
                nameof(source));
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        if (magic != DexProtocol.VideoMagic)
        {
            throw new DexProtocolException(
                $"Expected the DEXS stream magic but read 0x{magic:X8}. The agent on the device is " +
                "not the one this build expects, or another process is using the dexstream socket.");
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(source[4..6]);
        if (version != DexProtocol.Version)
        {
            throw new DexProtocolException(
                $"The agent speaks protocol version {version} but this build requires " +
                $"{DexProtocol.Version}. Remove {DexProtocol.AgentRemotePath} from the device and retry.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        string name = ReadTruncatedUtf8(source.Slice(8, DexProtocol.DeviceNameSize));

        int offset = 8 + DexProtocol.DeviceNameSize;
        var codec = (DexCodec)BinaryPrimitives.ReadUInt32BigEndian(source.Slice(offset, 4));
        int width = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset + 4, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset + 8, 4));
        int refreshRate = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset + 12, 4));
        int displayId = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset + 16, 4));

        if (width is <= 0 or > 16384 || height is <= 0 or > 16384)
        {
            throw new DexProtocolException($"The agent reported an implausible resolution {width}x{height}.");
        }

        return new DexStreamHeader(version, flags, name, codec, width, height, refreshRate, displayId);
    }

    internal static void WriteTruncatedUtf8(string value, Span<byte> field)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        // Reserve the last byte for the terminator and never split a multi-byte sequence.
        int maxBytes = field.Length - 1;
        int chars = value.Length;
        while (chars > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, chars)) > maxBytes)
        {
            chars--;
        }

        // Dropping the low half of a surrogate pair would leave a lone high surrogate, which UTF-8
        // encodes as U+FFFD. Trim the whole pair instead so the name stays a real prefix.
        if (chars > 0 && char.IsHighSurrogate(value[chars - 1]))
        {
            chars--;
        }

        Encoding.UTF8.GetBytes(value.AsSpan(0, chars), field);
    }

    internal static string ReadTruncatedUtf8(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
        {
            end = field.Length;
        }

        return Encoding.UTF8.GetString(field[..end]);
    }
}
