using System.Buffers.Binary;
using DexStream.Core.Protocol;
using Xunit;

namespace DexStream.Core.Tests;

public class DexStreamHeaderTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var header = new DexStreamHeader(
            DexProtocol.Version, 0, "Galaxy S25 Ultra", DexCodec.H265, 3840, 2160, 120_000, 2);
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];

        header.Write(buffer);

        Assert.Equal(header, DexStreamHeader.Parse(buffer));
    }

    [Fact]
    public void Write_UsesBigEndianSoTheJavaAgentNeedsNoByteSwapping()
    {
        var header = new DexStreamHeader(1, 0, "x", DexCodec.H264, 1920, 1080, 60_000, 0);
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];

        header.Write(buffer);

        Assert.Equal("DEXS", System.Text.Encoding.ASCII.GetString(buffer, 0, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2)));
        Assert.Equal("h264", System.Text.Encoding.ASCII.GetString(buffer, 72, 4));
        Assert.Equal(1920, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(76, 4)));
        Assert.Equal(1080, BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(80, 4)));
    }

    [Fact]
    public void RefreshRateHz_ConvertsFromMilliHertz()
        => Assert.Equal(
            119.998,
            new DexStreamHeader(1, 0, "x", DexCodec.H264, 1, 1, 119_998, 0).RefreshRateHz,
            precision: 3);

    [Fact]
    public void Parse_RejectsABadMagic()
    {
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];
        new DexStreamHeader(1, 0, "x", DexCodec.H264, 100, 100, 60_000, 0).Write(buffer);
        buffer[0] = (byte)'X';

        DexProtocolException error = Assert.Throws<DexProtocolException>(() => DexStreamHeader.Parse(buffer));
        Assert.Contains("DEXS", error.Message);
    }

    [Fact]
    public void Parse_RejectsAMismatchedVersion()
    {
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];
        new DexStreamHeader(1, 0, "x", DexCodec.H264, 100, 100, 60_000, 0).Write(buffer);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), 99);

        DexProtocolException error = Assert.Throws<DexProtocolException>(() => DexStreamHeader.Parse(buffer));
        Assert.Contains("protocol version 99", error.Message);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 20000)]
    public void Parse_RejectsImplausibleGeometry(int width, int height)
    {
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];
        new DexStreamHeader(1, 0, "x", DexCodec.H264, 640, 480, 60_000, 0).Write(buffer);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(76, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(80, 4), height);

        Assert.Throws<DexProtocolException>(() => DexStreamHeader.Parse(buffer));
    }

    [Fact]
    public void Write_TruncatesALongDeviceNameWithoutOverflowing()
    {
        string name = new('A', 200);
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];

        new DexStreamHeader(1, 0, name, DexCodec.H264, 640, 480, 60_000, 0).Write(buffer);

        DexStreamHeader parsed = DexStreamHeader.Parse(buffer);
        Assert.Equal(DexProtocol.DeviceNameSize - 1, parsed.DeviceName.Length);
        // The codec field immediately after the name must be intact.
        Assert.Equal(DexCodec.H264, parsed.Codec);
    }

    [Fact]
    public void Write_DoesNotSplitAMultiByteCharacter()
    {
        // 32 four-byte emoji is 128 bytes, well past the 63 usable bytes in the field.
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 32));
        byte[] buffer = new byte[DexProtocol.StreamHeaderSize];

        new DexStreamHeader(1, 0, name, DexCodec.H264, 640, 480, 60_000, 0).Write(buffer);

        DexStreamHeader parsed = DexStreamHeader.Parse(buffer);
        Assert.StartsWith(parsed.DeviceName, name, StringComparison.Ordinal);
        Assert.DoesNotContain('�', parsed.DeviceName);
    }

    [Fact]
    public void Write_RejectsAShortBuffer()
        => Assert.Throws<ArgumentException>(
            () => new DexStreamHeader(1, 0, "x", DexCodec.H264, 1, 1, 1, 0).Write(new byte[10]));
}

public class DexPacketHeaderTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var header = new DexPacketHeader(
            DexPacketType.Frame, DexPacketFlags.KeyFrame, 123456, 987_654_321_000);
        byte[] buffer = new byte[DexProtocol.PacketHeaderSize];

        header.Write(buffer);

        Assert.Equal(header, DexPacketHeader.Parse(buffer));
    }

    [Fact]
    public void IsCodecConfig_IsTrueForBothTheTypeAndTheFlag()
    {
        Assert.True(new DexPacketHeader(DexPacketType.Config, DexPacketFlags.None, 0, 0).IsCodecConfig);
        Assert.True(new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.CodecConfig, 0, 0).IsCodecConfig);
        Assert.False(new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.KeyFrame, 0, 0).IsCodecConfig);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(DexProtocol.MaxPacketSize + 1)]
    public void Parse_RejectsAnOutOfRangeLength(int length)
    {
        byte[] buffer = new byte[DexProtocol.PacketHeaderSize];
        new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.None, 0, 0).Write(buffer);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), length);

        Assert.Throws<DexProtocolException>(() => DexPacketHeader.Parse(buffer));
    }

    [Fact]
    public void Parse_AcceptsAZeroLengthHeartbeat()
    {
        byte[] buffer = new byte[DexProtocol.PacketHeaderSize];
        new DexPacketHeader(DexPacketType.Heartbeat, DexPacketFlags.None, 0, 42).Write(buffer);

        DexPacketHeader parsed = DexPacketHeader.Parse(buffer);

        Assert.Equal(DexPacketType.Heartbeat, parsed.Type);
        Assert.Equal(0, parsed.PayloadLength);
        Assert.Equal(42ul, parsed.PresentationTimeUs);
    }
}

public class DexVideoStreamReaderTests
{
    private static byte[] BuildStream(params (DexPacketHeader Header, byte[] Payload)[] packets)
    {
        using var buffer = new MemoryStream();
        byte[] streamHeader = new byte[DexProtocol.StreamHeaderSize];
        new DexStreamHeader(DexProtocol.Version, 0, "SM-S938B", DexCodec.H264, 2560, 1440, 120_000, 2)
            .Write(streamHeader);
        buffer.Write(streamHeader);

        foreach ((DexPacketHeader header, byte[] payload) in packets)
        {
            byte[] packetHeader = new byte[DexProtocol.PacketHeaderSize];
            header.Write(packetHeader);
            buffer.Write(packetHeader);
            buffer.Write(payload);
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task ReadsTheHeaderThenEveryPacketInOrder()
    {
        byte[] config = [0x00, 0x00, 0x00, 0x01, 0x67, 0x64];
        byte[] frame = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84];
        byte[] wire = BuildStream(
            (new DexPacketHeader(DexPacketType.Config, DexPacketFlags.CodecConfig, config.Length, 0), config),
            (new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.KeyFrame, frame.Length, 1000), frame));

        using var reader = new DexVideoStreamReader(new MemoryStream(wire));

        DexStreamHeader header = await reader.ReadStreamHeaderAsync();
        Assert.Equal(2560, header.Width);
        Assert.Equal("SM-S938B", header.DeviceName);

        DexPacket? first = await reader.ReadPacketAsync();
        Assert.True(first!.Value.Header.IsCodecConfig);
        Assert.Equal(config, first.Value.Payload.ToArray());

        DexPacket? second = await reader.ReadPacketAsync();
        Assert.True(second!.Value.Header.IsKeyFrame);
        Assert.Equal(frame, second.Value.Payload.ToArray());
        Assert.Equal(1000ul, second.Value.Header.PresentationTimeUs);

        Assert.Null(await reader.ReadPacketAsync());
    }

    [Fact]
    public async Task GrowsItsBufferForALargePacket()
    {
        byte[] big = new byte[900_000];
        Random.Shared.NextBytes(big);
        byte[] wire = BuildStream(
            (new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.KeyFrame, big.Length, 5), big));

        using var reader = new DexVideoStreamReader(new MemoryStream(wire), initialBufferSize: 4096);
        await reader.ReadStreamHeaderAsync();

        DexPacket? packet = await reader.ReadPacketAsync();

        Assert.Equal(big, packet!.Value.Payload.ToArray());
    }

    [Fact]
    public async Task ThrowsOnATruncatedPayload()
    {
        byte[] payload = new byte[64];
        byte[] wire = BuildStream(
            (new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.None, payload.Length, 0), payload));
        // Cut the last 10 bytes off the payload.
        byte[] truncated = wire[..^10];

        using var reader = new DexVideoStreamReader(new MemoryStream(truncated));
        await reader.ReadStreamHeaderAsync();

        await Assert.ThrowsAsync<DexProtocolException>(async () => await reader.ReadPacketAsync());
    }

    [Fact]
    public async Task ThrowsWhenTheStreamHeaderIsTruncated()
    {
        using var reader = new DexVideoStreamReader(new MemoryStream(new byte[20]));

        await Assert.ThrowsAsync<DexProtocolException>(async () => await reader.ReadStreamHeaderAsync());
    }

    [Fact]
    public async Task HandlesAStreamDeliveredOneByteAtATime()
    {
        byte[] frame = [1, 2, 3, 4, 5];
        byte[] wire = BuildStream(
            (new DexPacketHeader(DexPacketType.Frame, DexPacketFlags.KeyFrame, frame.Length, 7), frame));

        using var reader = new DexVideoStreamReader(new DripStream(wire));
        await reader.ReadStreamHeaderAsync();

        DexPacket? packet = await reader.ReadPacketAsync();

        Assert.Equal(frame, packet!.Value.Payload.ToArray());
    }

    /// <summary>A stream that returns a single byte per read, to exercise the read-exact loops.</summary>
    private sealed class DripStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = data[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = data[_position++];
            return ValueTask.FromResult(1);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
