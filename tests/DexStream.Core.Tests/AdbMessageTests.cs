using System.Buffers.Binary;
using System.Text;
using DexStream.Core.Adb;
using Xunit;

namespace DexStream.Core.Tests;

public class AdbMessageTests
{
    [Fact]
    public void WriteHeader_ProducesTheLayoutAdbdExpects()
    {
        byte[] payload = [1, 2, 3, 250];
        var message = new AdbMessage(AdbCommand.Write, 0xAABBCCDD, 0x11223344, payload);
        byte[] header = new byte[AdbProtocol.HeaderSize];

        message.WriteHeader(header);

        Assert.Equal((uint)AdbCommand.Write, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4)));
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)));
        Assert.Equal(
            (uint)AdbCommand.Write ^ 0xFFFFFFFFu,
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4)));
    }

    [Fact]
    public void CommandValues_AreTheAsciiFourCharacterCodes()
    {
        Assert.Equal("CNXN", FourCc(AdbCommand.Connect));
        Assert.Equal("AUTH", FourCc(AdbCommand.Auth));
        Assert.Equal("OPEN", FourCc(AdbCommand.Open));
        Assert.Equal("OKAY", FourCc(AdbCommand.Okay));
        Assert.Equal("CLSE", FourCc(AdbCommand.Close));
        Assert.Equal("WRTE", FourCc(AdbCommand.Write));
        Assert.Equal("SYNC", FourCc(AdbCommand.Sync));
        Assert.Equal("STLS", FourCc(AdbCommand.StartTls));

        static string FourCc(AdbCommand command)
            => Encoding.ASCII.GetString(BitConverter.GetBytes((uint)command));
    }

    [Fact]
    public void ParseHeader_RoundTripsWriteHeader()
    {
        var original = new AdbMessage(AdbCommand.Open, 7, 0, "shell:id\0"u8.ToArray());
        byte[] header = new byte[AdbProtocol.HeaderSize];
        original.WriteHeader(header);

        AdbMessageHeader parsed = AdbMessage.ParseHeader(header, 1024, out int payloadLength);

        Assert.Equal(AdbCommand.Open, parsed.Command);
        Assert.Equal(7u, parsed.Arg0);
        Assert.Equal(0u, parsed.Arg1);
        Assert.Equal(original.PayloadLength, payloadLength);
    }

    [Fact]
    public void ParseHeader_RejectsAMismatchedMagic()
    {
        byte[] header = new byte[AdbProtocol.HeaderSize];
        new AdbMessage(AdbCommand.Okay, 1, 2).WriteHeader(header);
        header[20] ^= 0xFF; // corrupt the magic

        AdbProtocolException error = Assert.Throws<AdbProtocolException>(
            () => AdbMessage.ParseHeader(header, 1024, out _));
        Assert.Contains("out of sync", error.Message);
    }

    [Fact]
    public void ParseHeader_RejectsAPayloadLargerThanNegotiated()
    {
        byte[] header = new byte[AdbProtocol.HeaderSize];
        new AdbMessage(AdbCommand.Write, 1, 2).WriteHeader(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 5000);

        AdbProtocolException error = Assert.Throws<AdbProtocolException>(
            () => AdbMessage.ParseHeader(header, 4096, out _));
        Assert.Contains("exceeds", error.Message);
    }

    [Fact]
    public void Checksum_IsTheUnsignedSumOfPayloadBytes()
        => Assert.Equal(255u + 1u + 0u, AdbMessage.Checksum([255, 1, 0]));

    [Fact]
    public void Open_AppendsTheTrailingNul()
    {
        AdbMessage message = AdbMessage.Open(3, "sync:");

        Assert.Equal(6, message.PayloadLength);
        Assert.Equal(0, message.Payload.Span[^1]);
        Assert.Equal("sync:", Encoding.UTF8.GetString(message.Payload.Span[..5]));
    }

    [Fact]
    public void Connect_AdvertisesTheVersionAndMaxPayload()
    {
        AdbMessage message = AdbMessage.Connect(4096, "host::features=x");

        Assert.Equal(AdbCommand.Connect, message.Command);
        Assert.Equal(AdbProtocol.Version, message.Arg0);
        Assert.Equal(4096u, message.Arg1);
    }

    [Fact]
    public void WriteHeader_RejectsAShortBuffer()
        => Assert.Throws<ArgumentException>(
            () => new AdbMessage(AdbCommand.Okay, 0, 0).WriteHeader(new byte[23]));
}
