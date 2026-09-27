using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.Core.Adb;

/// <summary>Options controlling a single <see cref="AdbConnection"/>.</summary>
public sealed class AdbConnectionOptions
{
    /// <summary>Payload size advertised to the device in the CNXN message.</summary>
    public int MaxPayload { get; init; } = AdbProtocol.DefaultMaxPayload;

    /// <summary>Buffers held per stream before the reader loop stops acknowledging WRTEs.</summary>
    public int ReceiveQueueCapacity { get; init; } = 96;

    /// <summary>
    /// How long the shared reader loop will wait for one stream's consumer before giving up on
    /// that stream. A single slow consumer must not stall the other streams forever.
    /// </summary>
    public TimeSpan StreamBackpressureTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the device's CNXN or an AUTH exchange to complete.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the user to tap <em>Allow</em> on the device's USB debugging prompt.
    /// </summary>
    public TimeSpan AuthorizationTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Identity presented on the device's authorization prompt.</summary>
    public string SystemIdentity { get; init; } = "host::features=" + AdbProtocol.HostFeatures;
}

/// <summary>
/// An authenticated ADB session over an <see cref="IAdbTransport"/>: performs the CNXN/AUTH
/// handshake and then multiplexes any number of <see cref="AdbStream"/>s over the single pipe.
/// </summary>
public sealed class AdbConnection : IAsyncDisposable
{
    private readonly IAdbTransport _transport;
    private readonly AdbConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, AdbStream> _streams = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly byte[] _sendHeader = new byte[AdbProtocol.HeaderSize];

    private uint _nextStreamId = 1;
    private Task? _readerLoop;
    private TaskCompletionSource<AdbMessage>? _handshakeWaiter;
    private bool _disposed;

    public AdbConnection(
        IAdbTransport transport,
        AdbConnectionOptions? options = null,
        ILogger<AdbConnection>? logger = null)
    {
        _transport = transport;
        _options = options ?? new AdbConnectionOptions();
        _logger = logger ?? NullLogger<AdbConnection>.Instance;
        MaxPayload = _options.MaxPayload;
    }

    /// <summary>
    /// The smaller of our own and the device's advertised payload size. WRTE payloads are
    /// chunked to this value.
    /// </summary>
    public int MaxPayload { get; private set; }

    /// <summary>The device's CNXN banner, for example <c>device::ro.product.name=dm3q...</c>.</summary>
    public string? DeviceBanner { get; private set; }

    /// <summary>True once the device has accepted our key and sent its CNXN.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised when the reader loop stops because of a transport or protocol error.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Performs the CNXN handshake, signing the device's auth token and, if the signature is
    /// rejected, offering our public key so the device can prompt the user.
    /// </summary>
    /// <exception cref="AdbAuthorizationException">The user did not authorize this host in time.</exception>
    public async Task ConnectAsync(AdbKeyPair keyPair, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _readerLoop = Task.Run(() => ReaderLoopAsync(_shutdown.Token), CancellationToken.None);

        await SendAsync(
            AdbMessage.Connect(_options.MaxPayload, _options.SystemIdentity),
            cancellationToken).ConfigureAwait(false);

        bool signatureOffered = false;
        bool publicKeyOffered = false;

        while (true)
        {
            TimeSpan timeout = publicKeyOffered ? _options.AuthorizationTimeout : _options.HandshakeTimeout;
            AdbMessage message = await AwaitHandshakeMessageAsync(timeout, publicKeyOffered, cancellationToken)
                .ConfigureAwait(false);

            switch (message.Command)
            {
                case AdbCommand.Connect:
                    ApplyDeviceConnect(message);
                    return;

                case AdbCommand.Auth when (AdbAuthType)message.Arg0 == AdbAuthType.Token:
                    if (message.PayloadLength != AdbProtocol.AuthTokenLength)
                    {
                        throw new AdbProtocolException(
                            $"Expected a {AdbProtocol.AuthTokenLength}-byte auth token, " +
                            $"got {message.PayloadLength} bytes.");
                    }

                    if (!signatureOffered)
                    {
                        signatureOffered = true;
                        _logger.LogDebug("Signing ADB auth token.");
                        byte[] signature = keyPair.SignToken(message.Payload.Span);
                        await SendAsync(
                            AdbMessage.Auth(AdbAuthType.Signature, signature),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (!publicKeyOffered)
                    {
                        publicKeyOffered = true;
                        _logger.LogInformation(
                            "Device rejected our signature; sending the public key. " +
                            "The phone will now ask the user to allow USB debugging.");
                        await SendAsync(
                            AdbMessage.Auth(AdbAuthType.RsaPublicKey, keyPair.EncodePublicKeyPayload()),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new AdbAuthorizationException(
                            "The device asked for authentication again after accepting our public key. " +
                            "Revoke USB debugging authorizations on the phone and reconnect.");
                    }

                    break;

                case AdbCommand.StartTls:
                    throw new AdbProtocolException(
                        "This device requires ADB over TLS, which DexStream does not implement. " +
                        "Disable 'Wireless debugging' on the phone and retry over USB.");

                default:
                    throw new AdbProtocolException($"Unexpected message during handshake: {message}.");
            }
        }
    }

    private void ApplyDeviceConnect(AdbMessage message)
    {
        if (message.Arg0 < AdbProtocol.MinimumVersion)
        {
            throw new AdbProtocolException(
                $"Device speaks ADB protocol 0x{message.Arg0:X8}, which is older than the " +
                $"supported minimum 0x{AdbProtocol.MinimumVersion:X8}.");
        }

        int deviceMax = message.Arg1 == 0 ? AdbProtocol.DefaultMaxPayload : (int)Math.Min(message.Arg1, int.MaxValue);
        MaxPayload = Math.Min(_options.MaxPayload, deviceMax);
        DeviceBanner = DecodeBanner(message.Payload.Span);
        IsConnected = true;
        _logger.LogInformation(
            "ADB connected over {Transport}. Banner: {Banner}. Max payload {MaxPayload} bytes.",
            _transport.Description,
            DeviceBanner,
            MaxPayload);
    }

    /// <summary>Opens an ADB service, for example <c>shell:id</c> or <c>sync:</c>.</summary>
    /// <exception cref="AdbProtocolException">The device refused the service with CLSE.</exception>
    public async Task<AdbStream> OpenStreamAsync(string service, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException("Call ConnectAsync before opening a stream.");
        }

        uint localId = NextStreamId();
        var stream = new AdbStream(this, localId, service, _options.ReceiveQueueCapacity);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingOpens[localId] = opened;
        _streams[localId] = stream;
        try
        {
            await SendAsync(AdbMessage.Open(localId, service), cancellationToken).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(_options.HandshakeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, cancellationToken, _shutdown.Token);
            using var registration = linked.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), opened);

            try
            {
                await opened.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new AdbProtocolException(
                    $"The device did not answer our request to open '{service}' within " +
                    $"{_options.HandshakeTimeout.TotalSeconds:0.#}s.");
            }

            return stream;
        }
        catch
        {
            _streams.TryRemove(localId, out _);
            throw;
        }
        finally
        {
            _pendingOpens.TryRemove(localId, out _);
        }
    }

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<bool>> _pendingOpens = new();

    /// <summary>Serialises one message onto the transport.</summary>
    internal async ValueTask SendAsync(AdbMessage message, CancellationToken cancellationToken)
    {
        if (message.PayloadLength > MaxPayload)
        {
            throw new ArgumentException(
                $"Payload of {message.PayloadLength} bytes exceeds the negotiated maximum of {MaxPayload}.",
                nameof(message));
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            message.WriteHeader(_sendHeader);
            await _transport.WriteAsync(_sendHeader, cancellationToken).ConfigureAwait(false);
            if (message.PayloadLength > 0)
            {
                await _transport.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal void DetachStream(AdbStream stream)
    {
        if (_streams.TryRemove(stream.LocalId, out _) && stream.RemoteId != 0 && _transport.IsConnected)
        {
            // Best effort: the peer may already have closed the stream.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(
                        AdbMessage.Close(stream.LocalId, stream.RemoteId),
                        _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send CLSE for stream {Service}.", stream.Service);
                }
            });
        }
    }

    private uint NextStreamId()
    {
        // Stream id 0 is reserved on the wire, so skip it if the counter ever wraps.
        uint id = Interlocked.Increment(ref _nextStreamId);
        return id == 0 ? Interlocked.Increment(ref _nextStreamId) : id;
    }

    private async Task<AdbMessage> AwaitHandshakeMessageAsync(
        TimeSpan timeout,
        bool waitingForUser,
        CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<AdbMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _handshakeWaiter, waiter);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token, cancellationToken, _shutdown.Token);
        using var registration = linked.Token.Register(
            static state => ((TaskCompletionSource<AdbMessage>)state!).TrySetCanceled(), waiter);

        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw waitingForUser
                ? new AdbAuthorizationException(
                    "Timed out waiting for USB debugging to be allowed on the phone. Unlock the screen, " +
                    "tap Allow on the 'Allow USB debugging?' prompt, and try again.")
                : new AdbProtocolException(
                    $"The device did not complete the ADB handshake within {timeout.TotalSeconds:0.#}s.");
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[AdbProtocol.HeaderSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _transport.ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
                AdbMessageHeader parsed = AdbMessage.ParseHeader(
                    header,
                    Math.Max(MaxPayload, _options.MaxPayload),
                    out int payloadLength);

                byte[] payload = payloadLength == 0 ? [] : new byte[payloadLength];
                if (payloadLength > 0)
                {
                    await _transport.ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
                }

                await DispatchAsync(parsed, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ADB reader loop stopped.");
            FaultAll(ex);
            Faulted?.Invoke(ex);
        }
    }

    private async ValueTask DispatchAsync(
        AdbMessageHeader header,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        switch (header.Command)
        {
            case AdbCommand.Connect:
            case AdbCommand.Auth:
            case AdbCommand.StartTls:
                Interlocked.Exchange(ref _handshakeWaiter, null)?.TrySetResult(
                    new AdbMessage(header.Command, header.Arg0, header.Arg1, payload));
                return;

            case AdbCommand.Okay:
                HandleOkay(header);
                return;

            case AdbCommand.Write:
                await HandleWriteAsync(header, payload, cancellationToken).ConfigureAwait(false);
                return;

            case AdbCommand.Close:
                HandleClose(header);
                return;

            case AdbCommand.Sync:
                // Only meaningful on the device side of the protocol; ignore.
                return;

            default:
                _logger.LogDebug("Ignoring unknown ADB command 0x{Command:X8}.", (uint)header.Command);
                return;
        }
    }

    private void HandleOkay(AdbMessageHeader header)
    {
        // arg0 is the device's stream id, arg1 is ours.
        if (!_streams.TryGetValue(header.Arg1, out AdbStream? stream))
        {
            return;
        }

        if (stream.RemoteId == 0)
        {
            stream.RemoteId = header.Arg0;
            if (_pendingOpens.TryGetValue(header.Arg1, out TaskCompletionSource<bool>? opened))
            {
                opened.TrySetResult(true);
            }

            return;
        }

        stream.CompleteWrite();
    }

    private async ValueTask HandleWriteAsync(
        AdbMessageHeader header,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(header.Arg1, out AdbStream? stream))
        {
            // Unknown stream: tell the device to stop sending.
            await SendAsync(AdbMessage.Close(header.Arg1, header.Arg0), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var backpressure = new CancellationTokenSource(_options.StreamBackpressureTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            backpressure.Token, cancellationToken);

        try
        {
            await stream.EnqueueAsync(payload, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (backpressure.IsCancellationRequested)
        {
            var error = new AdbProtocolException(
                $"Stream '{stream.Service}' was not drained within " +
                $"{_options.StreamBackpressureTimeout.TotalSeconds:0.#}s and was dropped to keep the " +
                "connection alive.");
            _logger.LogError(error, "Dropping stalled ADB stream {Service}.", stream.Service);
            stream.Fault(error);
            _streams.TryRemove(stream.LocalId, out _);
            await SendAsync(
                AdbMessage.Close(stream.LocalId, stream.RemoteId),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (ChannelClosedException)
        {
            // The consumer disposed the stream; nothing to acknowledge.
            return;
        }

        // Acknowledging only after the payload is queued gives the device real backpressure.
        await SendAsync(AdbMessage.Okay(header.Arg1, header.Arg0), cancellationToken).ConfigureAwait(false);
    }

    private void HandleClose(AdbMessageHeader header)
    {
        // A CLSE answering our OPEN carries arg0 == 0 and means the service was refused.
        if (_pendingOpens.TryGetValue(header.Arg1, out TaskCompletionSource<bool>? opened))
        {
            _streams.TryGetValue(header.Arg1, out AdbStream? refused);
            opened.TrySetException(new AdbProtocolException(
                $"The device refused to open service '{refused?.Service ?? "?"}'. " +
                "On Samsung devices this usually means USB debugging is off or the service name is unsupported."));
        }

        if (_streams.TryRemove(header.Arg1, out AdbStream? stream))
        {
            stream.MarkRemoteClosed();
        }
    }

    private void FaultAll(Exception error)
    {
        IsConnected = false;
        Interlocked.Exchange(ref _handshakeWaiter, null)?.TrySetException(error);

        foreach (TaskCompletionSource<bool> pending in _pendingOpens.Values)
        {
            pending.TrySetException(error);
        }

        foreach (AdbStream stream in _streams.Values)
        {
            stream.Fault(error);
        }

        _streams.Clear();
    }

    private static string DecodeBanner(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0 && payload[^1] == 0)
        {
            payload = payload[..^1];
        }

        return Encoding.UTF8.GetString(payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsConnected = false;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_readerLoop is not null)
        {
            try
            {
                await _readerLoop.ConfigureAwait(false);
            }
            catch
            {
                // The loop's failure has already been reported through Faulted.
            }
        }

        FaultAll(new ObjectDisposedException(nameof(AdbConnection)));
        await _transport.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _sendGate.Dispose();
    }
}
