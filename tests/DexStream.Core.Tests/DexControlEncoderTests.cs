using System.Buffers.Binary;
using System.Text;
using DexStream.Core.Protocol;
using Xunit;

namespace DexStream.Core.Tests;

public class DexControlEncoderTests
{
    [Fact]
    public void WriteHandshake_IsMagicThenVersion()
    {
        byte[] buffer = new byte[8];

        int written = DexControlEncoder.WriteHandshake(buffer);

        Assert.Equal(8, written);
        Assert.Equal("DEXC", Encoding.ASCII.GetString(buffer, 0, 4));
        Assert.Equal(DexProtocol.Version, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2)));
    }

    [Fact]
    public void WriteKeyEvent_MatchesTheDocumentedLayout()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        int written = DexControlEncoder.WriteKeyEvent(buffer, DexKeyAction.Down, 66, 3, 0x1001);

        Assert.Equal(14, written);
        Assert.Equal((byte)DexControlType.KeyEvent, buffer[0]);
        Assert.Equal((byte)DexKeyAction.Down, buffer[1]);
        Assert.Equal(66, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(2, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(6, 4)));
        Assert.Equal(0x1001, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(10, 4)));
    }

    [Fact]
    public void WritePointerEvent_MatchesTheDocumentedLayout()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        int written = DexControlEncoder.WritePointerEvent(
            buffer, DexPointerAction.Move, pointerId: -1, x: 1234, y: 567,
            displayWidth: 3840, displayHeight: 2160, pressure: 1.0f,
            actionButton: DexPointerButtons.None, buttons: DexPointerButtons.Primary);

        Assert.Equal(32, written);
        Assert.Equal((byte)DexControlType.PointerEvent, buffer[0]);
        Assert.Equal((byte)DexPointerAction.Move, buffer[1]);
        Assert.Equal(-1L, BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(2, 8)));
        Assert.Equal(1234, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(10, 4)));
        Assert.Equal(567, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(14, 4)));
        Assert.Equal(3840, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(18, 2)));
        Assert.Equal(2160, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(20, 2)));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(22, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(24, 4)));
        Assert.Equal((int)DexPointerButtons.Primary, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(28, 4)));
    }

    [Fact]
    public void WriteScroll_MatchesTheDocumentedLayout()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        int written = DexControlEncoder.WriteScroll(
            buffer, 10, 20, 1920, 1080, horizontalScroll: 0f, verticalScroll: -1f,
            DexPointerButtons.None);

        Assert.Equal(21, written);
        Assert.Equal((byte)DexControlType.Scroll, buffer[0]);
        Assert.Equal(10, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(1, 4)));
        Assert.Equal(20, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(5, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(13, 2)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(15, 2)));
    }

    [Fact]
    public void WriteText_IsLengthPrefixedUtf8()
    {
        const string text = "héllo \U0001F600";
        byte[] buffer = new byte[DexControlEncoder.MeasureText(text)];

        int written = DexControlEncoder.WriteText(buffer, text);

        Assert.Equal(buffer.Length, written);
        Assert.Equal((byte)DexControlType.Text, buffer[0]);
        int length = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(1, 4));
        Assert.Equal(Encoding.UTF8.GetByteCount(text), length);
        Assert.Equal(text, Encoding.UTF8.GetString(buffer, 5, length));
    }

    [Fact]
    public void WriteSetClipboard_CarriesTheSequenceAndPasteFlag()
    {
        const string text = "clip";
        byte[] buffer = new byte[DexControlEncoder.MeasureSetClipboard(text)];

        int written = DexControlEncoder.WriteSetClipboard(buffer, sequence: 77, paste: true, text);

        Assert.Equal(buffer.Length, written);
        Assert.Equal((byte)DexControlType.SetClipboard, buffer[0]);
        Assert.Equal(77L, BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(1, 8)));
        Assert.Equal(1, buffer[9]);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(10, 4)));
        Assert.Equal(text, Encoding.UTF8.GetString(buffer, 14, 4));
    }

    [Fact]
    public void WritePing_CarriesSequenceAndTimestamp()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        int written = DexControlEncoder.WritePing(buffer, 5, 1_234_567_890L);

        Assert.Equal(17, written);
        Assert.Equal(5L, BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(1, 8)));
        Assert.Equal(1_234_567_890L, BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(9, 8)));
    }

    [Fact]
    public void ShortMessages_AreASingleByteOrTwo()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        Assert.Equal(1, DexControlEncoder.WriteRequestKeyFrame(buffer));
        Assert.Equal((byte)DexControlType.RequestKeyFrame, buffer[0]);

        Assert.Equal(1, DexControlEncoder.WriteShutdown(buffer));
        Assert.Equal((byte)DexControlType.Shutdown, buffer[0]);

        Assert.Equal(2, DexControlEncoder.WriteSetDisplayPower(buffer, on: false));
        Assert.Equal(0, buffer[1]);

        Assert.Equal(2, DexControlEncoder.WriteSystemAction(buffer, DexSystemAction.Home));
        Assert.Equal((byte)DexSystemAction.Home, buffer[1]);

        Assert.Equal(5, DexControlEncoder.WriteSetBitrate(buffer, 25_000_000));
        Assert.Equal(25_000_000, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(1, 4)));
    }

    [Fact]
    public void EveryFixedMessageFitsInMaxFixedMessageSize()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        // WritePointerEvent is the largest; if it fits, so does everything else.
        int written = DexControlEncoder.WritePointerEvent(
            buffer, DexPointerAction.Down, 0, 0, 0, 1, 1, 1f,
            DexPointerButtons.None, DexPointerButtons.None);

        Assert.True(written <= DexControlEncoder.MaxFixedMessageSize);
    }

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 32767)]
    [InlineData(1f, 65535)]
    [InlineData(2f, 65535)]
    [InlineData(float.NaN, 0)]
    public void ToFixed16Unsigned_ClampsAndScales(float input, int expected)
        => Assert.Equal((ushort)expected, DexControlEncoder.ToFixed16Unsigned(input));

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32768)]
    [InlineData(5f, 32767)]
    [InlineData(-5f, -32768)]
    [InlineData(float.NaN, 0)]
    public void ToFixed16Signed_ClampsAndScales(float input, int expected)
        => Assert.Equal((short)expected, DexControlEncoder.ToFixed16Signed(input));

    [Fact]
    public void Writers_RejectABufferThatIsTooSmall()
        => Assert.Throws<ArgumentException>(
            () => DexControlEncoder.WriteKeyEvent(new byte[4], DexKeyAction.Down, 1, 0, 0));

    [Fact]
    public void WriteText_RejectsTextBeyondTheProtocolLimit()
    {
        string huge = new('x', DexControlEncoder.MaxTextBytes + 1);

        Assert.Throws<ArgumentException>(
            () => DexControlEncoder.WriteText(new byte[DexControlEncoder.MeasureText(huge)], huge));
    }

    [Fact]
    public void ClampToUInt16_HandlesOutOfRangeDisplaySizes()
    {
        byte[] buffer = new byte[DexControlEncoder.MaxFixedMessageSize];

        DexControlEncoder.WritePointerEvent(
            buffer, DexPointerAction.Down, 0, 0, 0,
            displayWidth: -5, displayHeight: 1_000_000, pressure: 1f,
            DexPointerButtons.None, DexPointerButtons.None);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(18, 2)));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(20, 2)));
    }
}
