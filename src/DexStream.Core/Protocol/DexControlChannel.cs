using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.Core.Protocol;

/// <summary>Result of a control-channel round trip.</summary>
/// <param name="Sequence">The ping sequence number this pong answers.</param>
/// <param name="HostSentUs">Host timestamp echoed back by the device.</param>
/// <param name="DeviceUs">The device's monotonic clock when it handled the ping.</param>
/// <param name="HostReceivedUs">Host timestamp when the pong arrived.</param>
public readonly record struct DexPong(long Sequence, long HostSentUs, long DeviceUs, long HostReceivedUs)
{
    /// <summary>Round-trip time in microseconds.</summary>
    public long RoundTripUs => HostReceivedUs - HostSentUs;
}

/// <summary>
/// The host end of the DexStream control channel: writes input and configuration messages to the
/// agent and reads its replies.
/// </summary>
/// <remarks>
/// Writes are serialised with a lock because input arrives on the UI thread while pings are sent
/// from a timer. Messages are small and the underlying ADB stream already batches, so a lock is
/// cheaper than a queue here.
/// </remarks>
public sealed class DexControlChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private long _pingSequence;
    private long _clipboardSequence;
    private bool _disposed;

    /// <summary>Encodes a fixed-size message into <paramref name="buffer"/> and returns its length.</summary>
    private delegate int EncodeFixedMessage(byte[] buffer);

    public DexControlChannel(Stream stream, ILogger<DexControlChannel>? logger = null)
    {
        _stream = stream;
        _logger = logger ?? NullLogger<DexControlChannel>.Instance;
    }

    /// <summary>Raised for every pong received by <see cref="RunReplyLoopAsync"/>.</summary>
    public event Action<DexPong>? PongReceived;

    /// <summary>Raised when the agent reports that it rejected a control message.</summary>
    public event Action<string>? AgentError;

    /// <summary>Sends the control channel handshake. Must be the first thing written.</summary>
    public Task SendHandshakeAsync(CancellationToken cancellationToken = default)
        => WriteFixedAsync(buffer => DexControlEncoder.WriteHandshake(buffer), cancellationToken);

    public Task SendKeyEventAsync(
        DexKeyAction action,
        int androidKeyCode,
        int metaState,
        int repeat = 0,
        CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WriteKeyEvent(buffer, action, androidKeyCode, repeat, metaState),
            cancellationToken);

    public Task SendPointerEventAsync(
        DexPointerAction action,
        long pointerId,
        int x,
        int y,
        int displayWidth,
        int displayHeight,
        float pressure,
        DexPointerButtons actionButton,
        DexPointerButtons buttons,
        CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WritePointerEvent(
                buffer, action, pointerId, x, y, displayWidth, displayHeight,
                pressure, actionButton, buttons),
            cancellationToken);

    public Task SendScrollAsync(
        int x,
        int y,
        int displayWidth,
        int displayHeight,
        float horizontalScroll,
        float verticalScroll,
        DexPointerButtons buttons,
        CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WriteScroll(
                buffer, x, y, displayWidth, displayHeight, horizontalScroll, verticalScroll, buttons),
            cancellationToken);

    public Task RequestKeyFrameAsync(CancellationToken cancellationToken = default)
        => WriteFixedAsync(buffer => DexControlEncoder.WriteRequestKeyFrame(buffer), cancellationToken);

    public Task SetBitrateAsync(int bitsPerSecond, CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WriteSetBitrate(buffer, bitsPerSecond), cancellationToken);

    public Task SetDisplayPowerAsync(bool on, CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WriteSetDisplayPower(buffer, on), cancellationToken);

    public Task SendSystemActionAsync(DexSystemAction action, CancellationToken cancellationToken = default)
        => WriteFixedAsync(
            buffer => DexControlEncoder.WriteSystemAction(buffer, action), cancellationToken);

    public Task ShutdownAgentAsync(CancellationToken cancellationToken = default)
        => WriteFixedAsync(buffer => DexControlEncoder.WriteShutdown(buffer), cancellationToken);

    /// <summary>Sends a ping and returns the sequence number to match against the pong.</summary>
    public async Task<long> SendPingAsync(long hostTimestampUs, CancellationToken cancellationToken = default)
    {
        long sequence = Interlocked.Increment(ref _pingSequence);
        await WriteFixedAsync(
            buffer => DexControlEncoder.WritePing(buffer, sequence, hostTimestampUs),
            cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        => WriteVariableAsync(
            DexControlEncoder.MeasureText(text),
            (buffer) => DexControlEncoder.WriteText(buffer, text),
            cancellationToken);

    public Task SetClipboardAsync(string text, bool paste, CancellationToken cancellationToken = default)
    {
        long sequence = Interlocked.Increment(ref _clipboardSequence);
        return WriteVariableAsync(
            DexControlEncoder.MeasureSetClipboard(text),
            (buffer) => DexControlEncoder.WriteSetClipboard(buffer, sequence, paste, text),
            cancellationToken);
    }

    /// <summary>
    /// Reads device replies until the channel closes. Run this on a background task; it raises
    /// <see cref="PongReceived"/> and <see cref="AgentError"/>.
    /// </summary>
    /// <param name="nowUs">Supplies the host clock, so tests can inject a deterministic one.</param>
    public async Task RunReplyLoopAsync(Func<long> nowUs, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[1];
        byte[] body = new byte[24];

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await TryReadExactAsync(header, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            switch ((DexControlReplyType)header[0])
            {
                case DexControlReplyType.Pong:
                {
                    if (!await TryReadExactAsync(body.AsMemory(0, 24), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    long sequence = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(0, 8));
                    long hostSent = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(8, 8));
                    long deviceUs = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(16, 8));
                    PongReceived?.Invoke(new DexPong(sequence, hostSent, deviceUs, nowUs()));
                    break;
                }

                case DexControlReplyType.Error:
                {
                    if (!await TryReadExactAsync(body.AsMemory(0, 4), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    int length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
                    if (length is < 0 or > DexControlEncoder.MaxTextBytes)
                    {
                        throw new DexProtocolException(
                            $"The agent sent a {length}-byte error message, which is out of range.");
                    }

                    byte[] text = new byte[length];
                    if (!await TryReadExactAsync(text, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    string message = Encoding.UTF8.GetString(text);
                    _logger.LogWarning("Device agent reported an error: {Message}", message);
                    AgentError?.Invoke(message);
                    break;
                }

                default:
                    throw new DexProtocolException(
                        $"Unknown control reply type 0x{header[0]:X2}; the control channel is out of sync.");
            }
        }
    }

    /// <summary>
    /// Encodes and sends one fixed-size message.
    /// </summary>
    /// <remarks>
    /// The buffer is rented per call rather than shared. Input arrives on the UI thread while pings are
    /// sent from a timer, so a shared buffer would let two encodes interleave and put a mangled message
    /// on the wire. These messages are at most 32 bytes, so the rental costs nothing measurable.
    /// </remarks>
    private async Task WriteFixedAsync(EncodeFixedMessage encode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(DexControlEncoder.MaxFixedMessageSize);
        try
        {
            int length = encode(buffer);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteVariableAsync(
        int size,
        Func<byte[], int> encode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int written = encode(buffer);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<bool> TryReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
