using System.Threading.Channels;

namespace DexStream.Core.Adb;

/// <summary>
/// One logical ADB stream, multiplexed over the shared transport by
/// <see cref="AdbConnection"/>. Corresponds to a single service such as
/// <c>shell:...</c>, <c>sync:</c> or <c>localabstract:...</c>.
/// </summary>
/// <remarks>
/// ADB allows a single unacknowledged WRTE per direction: after sending a WRTE we must wait
/// for the peer's OKAY before sending the next. <see cref="WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/>
/// enforces that, so callers can simply await it.
/// </remarks>
public sealed class AdbStream : Stream
{
    private readonly AdbConnection _connection;
    private readonly Channel<byte[]> _inbound;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private TaskCompletionSource<bool>? _writeAck;
    private ReadOnlyMemory<byte> _current;
    private bool _closed;
    private Exception? _fault;

    internal AdbStream(AdbConnection connection, uint localId, string service, int receiveQueueCapacity)
    {
        _connection = connection;
        LocalId = localId;
        Service = service;
        _inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(receiveQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Stream id chosen by this host.</summary>
    public uint LocalId { get; }

    /// <summary>Stream id assigned by the device, learned from its OKAY reply to our OPEN.</summary>
    public uint RemoteId { get; internal set; }

    /// <summary>The ADB service string this stream was opened with.</summary>
    public string Service { get; }

    /// <summary>True once either side has closed the stream.</summary>
    public bool IsClosed => _closed;

    public override bool CanRead => !_closed;

    public override bool CanSeek => false;

    public override bool CanWrite => !_closed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_current.Length == 0)
        {
            if (_fault is not null)
            {
                throw new AdbProtocolException($"Stream '{Service}' faulted.", _fault);
            }

            byte[]? chunk;
            try
            {
                chunk = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Clean end of stream: the peer sent CLSE and there is nothing buffered.
                return 0;
            }

            _current = chunk;
        }

        int count = Math.Min(buffer.Length, _current.Length);
        _current.Span[..count].CopyTo(buffer.Span);
        _current = _current[count..];
        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Sends <paramref name="buffer"/> to the device, splitting it into WRTE messages no larger
    /// than the negotiated payload size and waiting for each acknowledgement.
    /// </summary>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int maxChunk = _connection.MaxPayload;
            while (buffer.Length > 0)
            {
                ThrowIfClosed();
                int chunk = Math.Min(maxChunk, buffer.Length);

                var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _writeAck, ack);

                await _connection
                    .SendAsync(AdbMessage.Write(LocalId, RemoteId, buffer[..chunk]), cancellationToken)
                    .ConfigureAwait(false);

                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    ack);

                await ack.Task.ConfigureAwait(false);
                buffer = buffer[chunk..];
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
        // Every WriteAsync already waits for the device's OKAY, so nothing is buffered here.
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Queues an inbound payload, applying backpressure when the consumer falls behind.</summary>
    internal ValueTask EnqueueAsync(byte[] payload, CancellationToken cancellationToken)
        => _inbound.Writer.WriteAsync(payload, cancellationToken);

    /// <summary>Releases the pending <see cref="WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/>.</summary>
    internal void CompleteWrite()
        => Interlocked.Exchange(ref _writeAck, null)?.TrySetResult(true);

    /// <summary>Marks the stream closed by the peer and drains any waiter.</summary>
    internal void MarkRemoteClosed()
    {
        _closed = true;
        _inbound.Writer.TryComplete();
        Interlocked.Exchange(ref _writeAck, null)?.TrySetException(
            new AdbProtocolException($"The device closed stream '{Service}' while a write was in flight."));
    }

    /// <summary>Faults the stream, for example after a transport error.</summary>
    internal void Fault(Exception error)
    {
        _fault ??= error;
        _closed = true;
        _inbound.Writer.TryComplete(error);
        Interlocked.Exchange(ref _writeAck, null)?.TrySetException(error);
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new AdbProtocolException($"Stream '{Service}' is closed.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _inbound.Writer.TryComplete();
            _connection.DetachStream(this);
        }

        base.Dispose(disposing);
    }
}
