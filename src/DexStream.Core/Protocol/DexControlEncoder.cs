using System.Buffers.Binary;
using System.Text;

namespace DexStream.Core.Protocol;

/// <summary>Key event actions, matching Android's <c>KeyEvent.ACTION_*</c>.</summary>
public enum DexKeyAction : byte
{
    Down = 0,
    Up = 1,
}

/// <summary>Navigation shortcuts the agent performs on the streamed display.</summary>
public enum DexSystemAction : byte
{
    Back = 1,
    Home = 2,
    Recents = 3,
    Notifications = 4,
    ToggleKeyboard = 5,
}

/// <summary>
/// Serialises host-to-device control messages. Every integer is big-endian so the agent can read the
/// stream with a plain <c>DataInputStream</c>.
/// </summary>
/// <remarks>
/// Each method returns the number of bytes written, so the caller can send exactly that much. Sizes
/// are fixed per message type except for <see cref="WriteText"/> and
/// <see cref="WriteSetClipboard"/>, which carry a length-prefixed UTF-8 body.
/// </remarks>
public static class DexControlEncoder
{
    /// <summary>Largest message any of the fixed-size writers produces.</summary>
    public const int MaxFixedMessageSize = 32;

    /// <summary>Upper bound on the UTF-8 body of a text or clipboard message.</summary>
    public const int MaxTextBytes = 256 * 1024;

    /// <summary>Writes the 8-byte control channel handshake the host sends first.</summary>
    public static int WriteHandshake(Span<byte> destination)
    {
        Require(destination, 8);
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], DexProtocol.ControlMagic);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..6], DexProtocol.Version);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], 0);
        return 8;
    }

    public static int WriteKeyEvent(
        Span<byte> destination,
        DexKeyAction action,
        int androidKeyCode,
        int repeat,
        int metaState)
    {
        Require(destination, 14);
        destination[0] = (byte)DexControlType.KeyEvent;
        destination[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(destination[2..6], androidKeyCode);
        BinaryPrimitives.WriteInt32BigEndian(destination[6..10], repeat);
        BinaryPrimitives.WriteInt32BigEndian(destination[10..14], metaState);
        return 14;
    }

    public static int WritePointerEvent(
        Span<byte> destination,
        DexPointerAction action,
        long pointerId,
        int x,
        int y,
        int displayWidth,
        int displayHeight,
        float pressure,
        DexPointerButtons actionButton,
        DexPointerButtons buttons)
    {
        Require(destination, 32);
        destination[0] = (byte)DexControlType.PointerEvent;
        destination[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(destination[2..10], pointerId);
        BinaryPrimitives.WriteInt32BigEndian(destination[10..14], x);
        BinaryPrimitives.WriteInt32BigEndian(destination[14..18], y);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..20], ClampToUInt16(displayWidth));
        BinaryPrimitives.WriteUInt16BigEndian(destination[20..22], ClampToUInt16(displayHeight));
        BinaryPrimitives.WriteUInt16BigEndian(destination[22..24], ToFixed16Unsigned(pressure));
        BinaryPrimitives.WriteInt32BigEndian(destination[24..28], (int)actionButton);
        BinaryPrimitives.WriteInt32BigEndian(destination[28..32], (int)buttons);
        return 32;
    }

    public static int WriteScroll(
        Span<byte> destination,
        int x,
        int y,
        int displayWidth,
        int displayHeight,
        float horizontalScroll,
        float verticalScroll,
        DexPointerButtons buttons)
    {
        Require(destination, 21);
        destination[0] = (byte)DexControlType.Scroll;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..5], x);
        BinaryPrimitives.WriteInt32BigEndian(destination[5..9], y);
        BinaryPrimitives.WriteUInt16BigEndian(destination[9..11], ClampToUInt16(displayWidth));
        BinaryPrimitives.WriteUInt16BigEndian(destination[11..13], ClampToUInt16(displayHeight));
        BinaryPrimitives.WriteInt16BigEndian(destination[13..15], ToFixed16Signed(horizontalScroll));
        BinaryPrimitives.WriteInt16BigEndian(destination[15..17], ToFixed16Signed(verticalScroll));
        BinaryPrimitives.WriteInt32BigEndian(destination[17..21], (int)buttons);
        return 21;
    }

    public static int WriteRequestKeyFrame(Span<byte> destination)
    {
        Require(destination, 1);
        destination[0] = (byte)DexControlType.RequestKeyFrame;
        return 1;
    }

    public static int WriteSetBitrate(Span<byte> destination, int bitsPerSecond)
    {
        Require(destination, 5);
        destination[0] = (byte)DexControlType.SetBitrate;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..5], bitsPerSecond);
        return 5;
    }

    public static int WriteSetDisplayPower(Span<byte> destination, bool on)
    {
        Require(destination, 2);
        destination[0] = (byte)DexControlType.SetDisplayPower;
        destination[1] = on ? (byte)1 : (byte)0;
        return 2;
    }

    public static int WriteSystemAction(Span<byte> destination, DexSystemAction action)
    {
        Require(destination, 2);
        destination[0] = (byte)DexControlType.SystemAction;
        destination[1] = (byte)action;
        return 2;
    }

    public static int WritePing(Span<byte> destination, long sequence, long hostTimestampUs)
    {
        Require(destination, 17);
        destination[0] = (byte)DexControlType.Ping;
        BinaryPrimitives.WriteInt64BigEndian(destination[1..9], sequence);
        BinaryPrimitives.WriteInt64BigEndian(destination[9..17], hostTimestampUs);
        return 17;
    }

    public static int WriteShutdown(Span<byte> destination)
    {
        Require(destination, 1);
        destination[0] = (byte)DexControlType.Shutdown;
        return 1;
    }

    /// <summary>Writes a text-injection message. Returns the total byte count including the header.</summary>
    /// <exception cref="ArgumentException">The encoded text exceeds <see cref="MaxTextBytes"/>.</exception>
    public static int WriteText(Span<byte> destination, string text)
        => WriteLengthPrefixedText(destination, DexControlType.Text, prefix: [], text);

    /// <summary>Writes a clipboard update. <paramref name="paste"/> asks the agent to paste afterwards.</summary>
    public static int WriteSetClipboard(Span<byte> destination, long sequence, bool paste, string text)
    {
        Span<byte> prefix = stackalloc byte[9];
        BinaryPrimitives.WriteInt64BigEndian(prefix[0..8], sequence);
        prefix[8] = paste ? (byte)1 : (byte)0;
        return WriteLengthPrefixedText(destination, DexControlType.SetClipboard, prefix, text);
    }

    private static int WriteLengthPrefixedText(
        Span<byte> destination,
        DexControlType type,
        ReadOnlySpan<byte> prefix,
        string text)
    {
        int bodyLength = Encoding.UTF8.GetByteCount(text);
        if (bodyLength > MaxTextBytes)
        {
            throw new ArgumentException(
                $"Text of {bodyLength} bytes exceeds the {MaxTextBytes}-byte control message limit.",
                nameof(text));
        }

        int total = 1 + prefix.Length + 4 + bodyLength;
        Require(destination, total);

        destination[0] = (byte)type;
        prefix.CopyTo(destination[1..]);
        int offset = 1 + prefix.Length;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, 4), bodyLength);
        Encoding.UTF8.GetBytes(text, destination.Slice(offset + 4, bodyLength));
        return total;
    }

    /// <summary>Exact byte count <see cref="WriteText"/> will produce.</summary>
    public static int MeasureText(string text) => 1 + 4 + Encoding.UTF8.GetByteCount(text);

    /// <summary>Exact byte count <see cref="WriteSetClipboard"/> will produce.</summary>
    public static int MeasureSetClipboard(string text) => 1 + 9 + 4 + Encoding.UTF8.GetByteCount(text);

    /// <summary>Maps 0.0..1.0 onto the full unsigned 16-bit range, as the agent expects.</summary>
    internal static ushort ToFixed16Unsigned(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0;
        }

        return value >= 1f ? ushort.MaxValue : (ushort)(value * ushort.MaxValue);
    }

    /// <summary>Maps -1.0..1.0 onto the signed 16-bit range.</summary>
    internal static short ToFixed16Signed(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        if (value >= 1f)
        {
            return short.MaxValue;
        }

        if (value <= -1f)
        {
            return short.MinValue;
        }

        return (short)(value * short.MaxValue);
    }

    private static ushort ClampToUInt16(int value)
        => value <= 0 ? (ushort)0 : value >= ushort.MaxValue ? ushort.MaxValue : (ushort)value;

    private static void Require(Span<byte> destination, int needed)
    {
        if (destination.Length < needed)
        {
            throw new ArgumentException(
                $"Control message needs {needed} bytes, buffer holds {destination.Length}.",
                nameof(destination));
        }
    }
}
