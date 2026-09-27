using System.Threading.Channels;

namespace DexStream.Core.Tests;

/// <summary>
/// A one-way in-memory byte pipe with exact-length async reads, used to wire the ADB connection
/// under test to <see cref="FakeAdbDevice"/> without touching USB.
/// </summary>
internal sealed class BytePipe
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private ReadOnlyMemory<byte> _pending;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            _chunks.Writer.TryWrite(data.ToArray());
        }
    }

    public void Complete() => _chunks.Writer.TryComplete();

    /// <summary>Fills <paramref name="buffer"/>, throwing if the writer completes first.</summary>
    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            if (_pending.Length == 0)
            {
                try
                {
                    _pending = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    throw new EndOfStreamException(
                        $"Pipe closed after {offset} of {buffer.Length} bytes.");
                }
            }

            int count = Math.Min(buffer.Length - offset, _pending.Length);
            _pending.Span[..count].CopyTo(buffer.Span[offset..]);
            _pending = _pending[count..];
            offset += count;
        }
    }
}

/// <summary>An <see cref="DexStream.Core.Adb.IAdbTransport"/> backed by two <see cref="BytePipe"/>s.</summary>
internal sealed class InMemoryTransport : DexStream.Core.Adb.IAdbTransport
{
    private readonly BytePipe _outbound;
    private readonly BytePipe _inbound;

    public InMemoryTransport(BytePipe outbound, BytePipe inbound)
    {
        _outbound = outbound;
        _inbound = inbound;
    }

    public string Description => "in-memory";

    public int MaxTransferSize => 1024 * 1024;

    public bool IsConnected { get; private set; } = true;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        _outbound.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _inbound.ReadExactAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _outbound.Complete();
        return ValueTask.CompletedTask;
    }
}
