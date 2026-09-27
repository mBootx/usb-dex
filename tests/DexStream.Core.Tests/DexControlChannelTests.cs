using System.Buffers.Binary;
using System.Text;
using DexStream.Core.Protocol;
using Xunit;

namespace DexStream.Core.Tests;

public class DexControlChannelTests
{
    [Fact]
    public async Task ConcurrentSenders_ProduceIntactMessages()
    {
        // Input arrives on the UI thread while pings are sent from a timer, so the channel is written
        // from several threads at once. This checks that the resulting byte stream is well formed: it is
        // walked message by message, and a partial or interleaved write desynchronises the walk.
        //
        // It is a guard, not a reproduction. The window in which two encodes could collide is a few
        // instructions wide, so timing alone will not hit it reliably; what rules it out is that each
        // send now encodes into its own rented buffer, which makes a collision structurally impossible
        // rather than merely unlikely.
        var sink = new RecordingStream();
        await using var channel = new DexControlChannel(sink);

        await channel.SendHandshakeAsync();

        const int rounds = 200;
        Task[] senders =
        [
            Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    await channel.SendPointerEventAsync(
                        DexPointerAction.Move, -1, i, i * 2, 3840, 2160, 1f,
                        DexPointerButtons.None, DexPointerButtons.Primary);
                }
            }),
            Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    await channel.SendKeyEventAsync(DexKeyAction.Down, 29, 0x1000, 0);
                }
            }),
            Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    await channel.SendPingAsync(i * 1000L);
                }
            }),
            Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    await channel.SendScrollAsync(1, 2, 1920, 1080, 0f, -1f, DexPointerButtons.None);
                }
            }),
            Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    await channel.SendTextAsync("hello");
                }
            }),
        ];

        await Task.WhenAll(senders);

        Dictionary<DexControlType, int> counts = WalkMessages(sink.ToArray());

        Assert.Equal(rounds, counts[DexControlType.PointerEvent]);
        Assert.Equal(rounds, counts[DexControlType.KeyEvent]);
        Assert.Equal(rounds, counts[DexControlType.Ping]);
        Assert.Equal(rounds, counts[DexControlType.Scroll]);
        Assert.Equal(rounds, counts[DexControlType.Text]);
    }

    [Fact]
    public async Task Handshake_IsTheFirstThingOnTheWire()
    {
        var sink = new RecordingStream();
        await using var channel = new DexControlChannel(sink);

        await channel.SendHandshakeAsync();

        byte[] written = sink.ToArray();
        Assert.Equal(8, written.Length);
        Assert.Equal("DEXC", Encoding.ASCII.GetString(written, 0, 4));
    }

    [Fact]
    public async Task ReplyLoop_RaisesPongWithTheHostReceiveTime()
    {
        // A pong is one type byte plus sequence, echoed host time and device time.
        byte[] reply = new byte[1 + 24];
        reply[0] = (byte)DexControlReplyType.Pong;
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(1, 8), 7);
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(9, 8), 1_000);
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(17, 8), 5_000_000);

        await using var channel = new DexControlChannel(new RecordingStream(reply));

        DexPong? received = null;
        channel.PongReceived += pong => received = pong;

        await channel.RunReplyLoopAsync(() => 2_000);

        Assert.NotNull(received);
        Assert.Equal(7, received!.Value.Sequence);
        Assert.Equal(1_000, received.Value.HostSentUs);
        Assert.Equal(5_000_000, received.Value.DeviceUs);
        Assert.Equal(2_000, received.Value.HostReceivedUs);
        Assert.Equal(1_000, received.Value.RoundTripUs);
    }

    [Fact]
    public async Task ReplyLoop_RaisesAgentErrorText()
    {
        byte[] body = Encoding.UTF8.GetBytes("clipboard unavailable");
        byte[] reply = new byte[1 + 4 + body.Length];
        reply[0] = (byte)DexControlReplyType.Error;
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(1, 4), body.Length);
        body.CopyTo(reply, 5);

        await using var channel = new DexControlChannel(new RecordingStream(reply));

        string? message = null;
        channel.AgentError += text => message = text;

        await channel.RunReplyLoopAsync(() => 0);

        Assert.Equal("clipboard unavailable", message);
    }

    [Fact]
    public async Task ReplyLoop_ThrowsOnAnUnknownReplyType()
    {
        await using var channel = new DexControlChannel(new RecordingStream([0x7F]));

        await Assert.ThrowsAsync<DexProtocolException>(() => channel.RunReplyLoopAsync(() => 0));
    }

    /// <summary>
    /// Walks a control byte stream, returning how many of each message type it contains.
    /// </summary>
    /// <remarks>
    /// Every message's length is implied by its type, so walking the stream is only possible if each
    /// message was written whole and in order. A partial, interleaved or reordered write desynchronises
    /// the walk, which then either reads an unknown type or runs off the end.
    /// </remarks>
    private static Dictionary<DexControlType, int> WalkMessages(byte[] stream)
    {
        var counts = new Dictionary<DexControlType, int>();
        int offset = 8; // skip the handshake

        while (offset < stream.Length)
        {
            var type = (DexControlType)stream[offset];

            int length = type switch
            {
                DexControlType.KeyEvent => 14,
                DexControlType.PointerEvent => 32,
                DexControlType.Scroll => 21,
                DexControlType.Ping => 17,
                DexControlType.RequestKeyFrame => 1,
                DexControlType.Shutdown => 1,
                DexControlType.SetBitrate => 5,
                DexControlType.SetDisplayPower => 2,
                DexControlType.SystemAction => 2,
                DexControlType.Text =>
                    1 + 4 + BinaryPrimitives.ReadInt32BigEndian(stream.AsSpan(offset + 1, 4)),
                DexControlType.SetClipboard =>
                    1 + 9 + 4 + BinaryPrimitives.ReadInt32BigEndian(stream.AsSpan(offset + 10, 4)),
                _ => throw new InvalidOperationException(
                    $"Unknown control type 0x{stream[offset]:X2} at offset {offset}: the stream is " +
                    "out of sync, which means a message was not written atomically."),
            };

            Assert.True(
                offset + length <= stream.Length,
                $"A {type} message at offset {offset} claims {length} bytes but only " +
                $"{stream.Length - offset} remain.");

            counts[type] = counts.GetValueOrDefault(type) + 1;
            offset += length;
        }

        return counts;
    }

    /// <summary>A stream that records everything written and can replay canned reply bytes.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly byte[] _toRead;
        private int _readOffset;

        public RecordingStream(byte[]? toRead = null) => _toRead = toRead ?? [];

        public byte[] ToArray()
        {
            lock (_written)
            {
                return _written.ToArray();
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_written)
            {
                _written.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_written)
            {
                _written.Write(buffer, offset, count);
            }
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readOffset >= _toRead.Length || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            int count = Math.Min(buffer.Length, _toRead.Length - _readOffset);
            _toRead.AsSpan(_readOffset, count).CopyTo(buffer.Span);
            _readOffset += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
