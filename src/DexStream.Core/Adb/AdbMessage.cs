using System.Buffers.Binary;
using System.Text;

namespace DexStream.Core.Adb;

/// <summary>
/// A single ADB wire message: a 24-byte little-endian header plus an optional payload.
/// </summary>
/// <remarks>
/// Header layout (all fields <c>uint32</c>, little-endian):
/// <c>command, arg0, arg1, data_length, data_checksum, magic</c> where
/// <c>magic == command ^ 0xFFFFFFFF</c>.
/// </remarks>
public readonly struct AdbMessage
{
    public AdbMessage(AdbCommand command, uint arg0, uint arg1, ReadOnlyMemory<byte> payload = default)
    {
        Command = command;
        Arg0 = arg0;
        Arg1 = arg1;
        Payload = payload;
    }

    public AdbCommand Command { get; }

    public uint Arg0 { get; }

    public uint Arg1 { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public int PayloadLength => Payload.Length;

    /// <summary>
    /// adbd's payload checksum: the unsigned sum of every payload byte. Protocol
    /// versions at or above <c>0x01000001</c> ignore it, but older devices validate
    /// it, and computing it costs almost nothing.
    /// </summary>
    public static uint Checksum(ReadOnlySpan<byte> payload)
    {
        uint sum = 0;
        foreach (byte b in payload)
        {
            sum += b;
        }

        return sum;
    }

    /// <summary>Writes the 24-byte header for this message into <paramref name="destination"/>.</summary>
    public void WriteHeader(Span<byte> destination)
    {
        if (destination.Length < AdbProtocol.HeaderSize)
        {
            throw new ArgumentException(
                $"Header buffer must be at least {AdbProtocol.HeaderSize} bytes.",
                nameof(destination));
        }

        ReadOnlySpan<byte> payload = Payload.Span;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], (uint)Command);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], Arg0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], Arg1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], Checksum(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], (uint)Command ^ 0xFFFF_FFFFu);
    }

    /// <summary>
    /// Parses a 24-byte header, validating the magic field. The payload is not read here;
    /// the caller issues a second transport read of <paramref name="payloadLength"/> bytes.
    /// </summary>
    /// <exception cref="AdbProtocolException">
    /// The magic field does not match the command, or the advertised payload length exceeds
    /// <paramref name="maxPayload"/>.
    /// </exception>
    public static AdbMessageHeader ParseHeader(ReadOnlySpan<byte> header, int maxPayload, out int payloadLength)
    {
        if (header.Length < AdbProtocol.HeaderSize)
        {
            throw new ArgumentException(
                $"Header buffer must be at least {AdbProtocol.HeaderSize} bytes.",
                nameof(header));
        }

        uint command = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        uint arg0 = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        uint arg1 = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);

        if ((command ^ 0xFFFF_FFFFu) != magic)
        {
            throw new AdbProtocolException(
                $"Corrupt ADB header: command 0x{command:X8} does not match magic 0x{magic:X8}. " +
                "This usually means the USB pipe is out of sync; reconnect the device.");
        }

        if (length > (uint)maxPayload)
        {
            throw new AdbProtocolException(
                $"Device announced a {length}-byte payload which exceeds the negotiated maximum of {maxPayload} bytes.");
        }

        payloadLength = (int)length;
        return new AdbMessageHeader((AdbCommand)command, arg0, arg1, (int)length, checksum);
    }

    public override string ToString()
    {
        string name = Enum.IsDefined(typeof(AdbCommand), Command)
            ? Command.ToString()
            : $"0x{(uint)Command:X8}";
        return $"{name}(arg0=0x{Arg0:X}, arg1=0x{Arg1:X}, len={PayloadLength})";
    }

    /// <summary>Builds the host CNXN message, including the trailing NUL adbd expects.</summary>
    public static AdbMessage Connect(int maxPayload, string systemIdentity)
    {
        byte[] banner = NullTerminated(systemIdentity);
        return new AdbMessage(AdbCommand.Connect, AdbProtocol.Version, (uint)maxPayload, banner);
    }

    /// <summary>Builds an OPEN message for an ADB service such as <c>shell:</c> or <c>sync:</c>.</summary>
    public static AdbMessage Open(uint localId, string service)
        => new(AdbCommand.Open, localId, 0, NullTerminated(service));

    public static AdbMessage Okay(uint localId, uint remoteId)
        => new(AdbCommand.Okay, localId, remoteId);

    public static AdbMessage Close(uint localId, uint remoteId)
        => new(AdbCommand.Close, localId, remoteId);

    public static AdbMessage Write(uint localId, uint remoteId, ReadOnlyMemory<byte> data)
        => new(AdbCommand.Write, localId, remoteId, data);

    public static AdbMessage Auth(AdbAuthType type, ReadOnlyMemory<byte> data)
        => new(AdbCommand.Auth, (uint)type, 0, data);

    internal static byte[] NullTerminated(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] buffer = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, buffer);
        buffer[byteCount] = 0;
        return buffer;
    }
}

/// <summary>The parsed header of an inbound ADB message.</summary>
public readonly record struct AdbMessageHeader(
    AdbCommand Command,
    uint Arg0,
    uint Arg1,
    int PayloadLength,
    uint Checksum);
