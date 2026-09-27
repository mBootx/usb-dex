using System.Globalization;
using System.IO;
using System.Text;
using DexStream.Core.Adb;
using DexStream.Core.Device;
using DexStream.Core.Input;
using DexStream.Core.Metrics;
using DexStream.Core.Protocol;
using DexStream.Core.Rendering;
using DexStream.Core.Session;
using DexStream.Media;
using DexStream.Usb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.App.Services;

/// <summary>
/// One streaming session: USB transport, ADB handshake, agent deployment, and the decode and present
/// loop, plus the control channel that carries input back to the device.
/// </summary>
/// <remarks>
/// <para>
/// The session owns a dedicated video thread. Everything on the hot path — reading a packet from the
/// ADB stream, decoding it and presenting it — happens on that one thread with no queue between the
/// stages, because any queue is latency the user feels as input lag. The UI thread only reads
/// snapshots (status, metrics, viewport) and writes input.
/// </para>
/// <para>
/// Setup order matters and is not obvious: the agent must be running and listening before the host
/// can open its socket, and the video connection must be accepted before the control connection,
/// because the order of the two accepts is how the agent tells them apart.
/// </para>
/// </remarks>
public sealed class DexStreamSession : IAsyncDisposable
{
    /// <summary>How long to keep retrying the agent's socket while it starts up.</summary>
    private static readonly TimeSpan AgentSocketTimeout = TimeSpan.FromSeconds(10);

    private readonly UsbDeviceInfo _device;
    private readonly StreamSessionOptions _options;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StreamMetrics _metrics = new();
    private readonly ClockSynchronizer _clock = new();

    private WinUsbAdbTransport? _transport;
    private AdbConnection? _connection;
    private AdbStream? _agentShell;
    private AdbStream? _videoStream;
    private DexControlChannel? _control;
    private D3D11VideoPipeline? _pipeline;
    private Thread? _videoThread;
    private Task? _controlReplyLoop;
    private Task? _pingLoop;
    private Task? _agentLogLoop;

    private volatile int _pendingClientWidth;
    private volatile int _pendingClientHeight;
    private volatile Viewport _viewport = Viewport.Empty;
    private volatile int _streamWidth;
    private volatile int _streamHeight;
    private ScalingMode _scaling;
    private bool _disposed;

    public DexStreamSession(
        UsbDeviceInfo device,
        StreamSessionOptions options,
        IntPtr windowHandle,
        int clientWidth,
        int clientHeight,
        ILoggerFactory? loggerFactory = null)
    {
        _device = device;
        _options = options;
        WindowHandle = windowHandle;
        _pendingClientWidth = clientWidth;
        _pendingClientHeight = clientHeight;
        _scaling = options.Scaling;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DexStreamSession>();
    }

    /// <summary>Raised whenever the session's state changes. Fired on a background thread.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised with each line the device agent writes to stderr.</summary>
    public event Action<string>? AgentLog;

    public IntPtr WindowHandle { get; }

    public UsbDeviceInfo Device => _device;

    /// <summary>The device's model, once ADB is up.</summary>
    public string? Model { get; private set; }

    /// <summary>Whether this model is known to support DeX.</summary>
    public DexSupportInfo Support { get; private set; }

    /// <summary>The display being captured, once it has been chosen.</summary>
    public DisplayInfo? CaptureDisplay { get; private set; }

    /// <summary>Why that display was chosen.</summary>
    public DexSelectionReason SelectionReason { get; private set; }

    /// <summary>The decoder in use, for the diagnostics pane.</summary>
    public string? DecoderName { get; private set; }

    /// <summary>The encoded frame size, which is what input coordinates are expressed against.</summary>
    public (int Width, int Height) StreamSize => (_streamWidth, _streamHeight);

    /// <summary>Where the image sits in the window. Safe to read from the UI thread.</summary>
    public Viewport Viewport => _viewport;

    public SessionStatus Status { get; private set; } =
        new(SessionState.DeviceReady, "Device connected.");

    /// <summary>A snapshot of the current performance figures.</summary>
    public MetricsSnapshot Metrics => _metrics.Snapshot();

    /// <summary>How the image is fitted into the window.</summary>
    public ScalingMode Scaling
    {
        get => _scaling;
        set
        {
            _scaling = value;
            if (_pipeline is not null)
            {
                // Applied by the video thread on its next pass.
                _pipeline.Scaling = value;
            }
        }
    }

    /// <summary>
    /// Brings the session up: opens USB, authenticates, deploys and starts the agent, then begins
    /// streaming on a dedicated thread. Returns once frames are flowing or setup has failed.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken token = linked.Token;

        try
        {
            Report(SessionState.Connecting, $"Opening the ADB interface on {_device.Description}...");
            _transport = WinUsbAdbTransport.Open(_device, _loggerFactory.CreateLogger<WinUsbAdbTransport>());

            var keyStore = new AdbKeyStore(_loggerFactory.CreateLogger<AdbKeyStore>());
            (AdbKeyPair key, AdbKeyStore.KeyOrigin origin) = keyStore.Load(reuseSystemKey: true);

            using (key)
            {
                if (origin == AdbKeyStore.KeyOrigin.NewlyGenerated)
                {
                    Report(
                        SessionState.AwaitingAuthorization,
                        "Unlock the phone and tap Allow on the \"Allow USB debugging?\" prompt.");
                }

                _connection = new AdbConnection(
                    _transport,
                    new AdbConnectionOptions(),
                    _loggerFactory.CreateLogger<AdbConnection>());
                _connection.Faulted += OnConnectionFaulted;

                await _connection.ConnectAsync(key, token).ConfigureAwait(false);
            }

            await IdentifyDeviceAsync(token).ConfigureAwait(false);
            DisplayInfo display = await ChooseDisplayAsync(token).ConfigureAwait(false);

            Report(SessionState.StartingAgent, "Installing the DexStream agent on the device...");
            await DeployAgentAsync(token).ConfigureAwait(false);
            await LaunchAgentAsync(display, token).ConfigureAwait(false);

            (AdbStream video, AdbStream control) = await OpenAgentChannelsAsync(token).ConfigureAwait(false);
            _videoStream = video;

            _control = new DexControlChannel(control, _loggerFactory.CreateLogger<DexControlChannel>());
            _control.PongReceived += OnPong;
            _control.AgentError += message => Report(Status.State, Status.Message, message);
            await _control.SendHandshakeAsync(token).ConfigureAwait(false);

            _controlReplyLoop = Task.Run(
                () => _control.RunReplyLoopAsync(() => _metrics.NowUs, _shutdown.Token), CancellationToken.None);
            _pingLoop = Task.Run(() => RunPingLoopAsync(_shutdown.Token), CancellationToken.None);

            if (_options.TurnPhoneScreenOff)
            {
                await _control.SetDisplayPowerAsync(false, token).ConfigureAwait(false);
            }

            _videoThread = new Thread(VideoThread)
            {
                Name = "DexStream video",
                IsBackground = true,
                // The decode and present loop is the latency-critical path in the process.
                Priority = ThreadPriority.AboveNormal,
            };
            _videoThread.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(SessionState.Failed, Describe(ex), ex.ToString());
            throw;
        }
    }

    private async Task IdentifyDeviceAsync(CancellationToken token)
    {
        var shell = new AdbShellClient(_connection!);

        Model = await shell.GetPropertyAsync("ro.product.model", token).ConfigureAwait(false);
        string? manufacturer = await shell
            .GetPropertyAsync("ro.product.manufacturer", token)
            .ConfigureAwait(false);

        Support = SamsungDeviceCatalog.Lookup(Model, manufacturer);
        _logger.LogInformation(
            "Device identified as {Model} ({Manufacturer}); DeX support: {Level}.",
            Model, manufacturer, Support.Level);
    }

    private async Task<DisplayInfo> ChooseDisplayAsync(CancellationToken token)
    {
        var shell = new AdbShellClient(_connection!);
        string dump = await shell.RunAsync("dumpsys display", token).ConfigureAwait(false);

        IReadOnlyList<DisplayInfo> displays = DumpsysDisplayParser.Parse(dump);
        _logger.LogInformation("Device reports {Count} display(s): {Displays}", displays.Count, string.Join("; ", displays));

        if (_options.DisplayId is { } requested)
        {
            DisplayInfo? explicitChoice = displays.FirstOrDefault(d => d.DisplayId == requested);
            if (explicitChoice is null)
            {
                throw new InvalidOperationException(
                    $"Display {requested} was requested but the device only reports " +
                    $"{string.Join(", ", displays.Select(d => d.DisplayId))}.");
            }

            CaptureDisplay = explicitChoice;
            SelectionReason = DexSelectionReason.SecondaryDisplay;
            return explicitChoice;
        }

        DexDisplaySelection selection = DexDisplaySelector.Select(
            displays,
            DexProtocol.AgentDisplayName,
            allowPrimaryFallback: !_options.RequireDexDisplay || _options.ActivateDexIfMissing);

        if (selection.Display is null)
        {
            throw new InvalidOperationException(
                "No DeX display was found. Start DeX on the phone (connect it to a monitor, a DeX " +
                "dock, or use DeX on a wireless display), or turn off \"Require the DeX desktop\" in " +
                "settings to mirror the phone screen instead.");
        }

        CaptureDisplay = selection.Display;
        SelectionReason = selection.Reason;
        _logger.LogInformation("{Description}", DexDisplaySelector.Describe(selection));
        return selection.Display;
    }

    private async Task DeployAgentAsync(CancellationToken token)
    {
        (byte[] content, AgentPackage.Source origin) = AgentPackage.Load(_logger);
        _logger.LogInformation(
            "Pushing the device agent ({Bytes} bytes, from {Origin}) to {Path}.",
            content.Length, origin, DexProtocol.AgentRemotePath);

        await new AdbSyncClient(_connection!)
            .PushAsync(content, DexProtocol.AgentRemotePath, cancellationToken: token)
            .ConfigureAwait(false);
    }

    private async Task LaunchAgentAsync(DisplayInfo display, CancellationToken token)
    {
        var shell = new AdbShellClient(_connection!);

        // A previous agent still holding the abstract socket would make the new one fail to bind, and
        // that failure reads as a mysterious timeout. Clearing it first is cheap and idempotent.
        await shell.RunAsync("pkill -f com.dexstream.agent.Main", token).ConfigureAwait(false);

        string command = BuildAgentCommand(display);
        _logger.LogInformation("Starting the agent: {Command}", command);

        // The agent's own stream stays open for its lifetime: closing it would kill the process, and
        // its stderr is the only place capture failures are explained.
        _agentShell = await _connection!.OpenStreamAsync($"shell:{command}", token).ConfigureAwait(false);
        _agentLogLoop = Task.Run(() => PumpAgentLogAsync(_agentShell, _shutdown.Token), CancellationToken.None);
    }

    /// <summary>Builds the <c>app_process</c> command line that starts the agent.</summary>
    internal string BuildAgentCommand(DisplayInfo display)
    {
        DisplayMode mode = display.BestMode;
        int refreshRate = _options.TargetRefreshRate > 0
            ? _options.TargetRefreshRate
            : (int)Math.Round(display.MaxRefreshRate > 1 ? display.MaxRefreshRate : 60);

        DexCodec codec = _options.ResolveCodec(mode.Width, mode.Height);
        int bitrate = _options.Bitrate > 0
            ? _options.Bitrate
            : BitrateCalculator.Recommend(mode.Width, mode.Height, refreshRate, codec);

        string codecName = codec switch
        {
            DexCodec.H265 => "h265",
            DexCodec.Av1 => "av01",
            _ => "h264",
        };

        var arguments = new StringBuilder();
        arguments.Append(CultureInfo.InvariantCulture, $"display_id={display.DisplayId}");
        arguments.Append(CultureInfo.InvariantCulture, $" max_size={_options.MaxSize}");
        arguments.Append(CultureInfo.InvariantCulture, $" bitrate={bitrate}");
        arguments.Append(CultureInfo.InvariantCulture, $" max_fps={refreshRate}");
        arguments.Append(CultureInfo.InvariantCulture, $" codec={codecName}");
        arguments.Append(CultureInfo.InvariantCulture, $" turn_screen_off={Lower(_options.TurnPhoneScreenOff)}");
        arguments.Append(
            CultureInfo.InvariantCulture,
            $" create_desktop_display={Lower(_options.ActivateDexIfMissing)}");
        arguments.Append(CultureInfo.InvariantCulture, $" desktop_width={mode.Width}");
        arguments.Append(CultureInfo.InvariantCulture, $" desktop_height={mode.Height}");
        arguments.Append(
            CultureInfo.InvariantCulture,
            $" desktop_density={(display.DensityDpi > 0 ? display.DensityDpi : 240)}");

        return $"CLASSPATH={DexProtocol.AgentRemotePath} app_process / com.dexstream.agent.Main {arguments}";

        static string Lower(bool value) => value ? "true" : "false";
    }

    /// <summary>
    /// Opens the agent's two channels, retrying while the agent starts up.
    /// </summary>
    /// <remarks>
    /// The agent needs a moment to bind its abstract socket, and adbd answers an OPEN for a socket
    /// that does not exist yet with CLSE. Retrying is therefore the normal path, not error handling.
    /// </remarks>
    private async Task<(AdbStream Video, AdbStream Control)> OpenAgentChannelsAsync(CancellationToken token)
    {
        string service = $"localabstract:{DexProtocol.SocketName}";
        DateTime deadline = DateTime.UtcNow + AgentSocketTimeout;
        Exception? lastFailure = null;

        AdbStream? video = null;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    video ??= await _connection!.OpenStreamAsync(service, token).ConfigureAwait(false);
                    AdbStream control = await _connection!.OpenStreamAsync(service, token).ConfigureAwait(false);
                    return (video, control);
                }
                catch (AdbProtocolException ex)
                {
                    lastFailure = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(150), token).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            video?.Dispose();
            throw;
        }

        video?.Dispose();

        throw new TimeoutException(
            $"The device agent did not start listening within {AgentSocketTimeout.TotalSeconds:0}s. " +
            "The agent log in the diagnostics pane usually says why; a capture failure on Android 14 " +
            "or later is the most common cause.",
            lastFailure);
    }

    private async Task PumpAgentLogAsync(AdbStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        var pending = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                EmitCompleteLines(pending);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The session is shutting down, or the agent exited.
        }

        if (pending.Length > 0)
        {
            AgentLog?.Invoke(pending.ToString().TrimEnd());
        }
    }

    private void EmitCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            string text = pending.ToString();
            int newline = text.IndexOf('\n', StringComparison.Ordinal);
            if (newline < 0)
            {
                return;
            }

            string line = text[..newline].TrimEnd('\r');
            pending.Remove(0, newline + 1);

            if (line.Length > 0)
            {
                _logger.LogDebug("agent: {Line}", line);
                AgentLog?.Invoke(line);
            }
        }
    }

    /// <summary>The decode and present loop. Owns the Direct3D pipeline for the session's lifetime.</summary>
    private void VideoThread()
    {
        CancellationToken token = _shutdown.Token;

        try
        {
            using var reader = new DexVideoStreamReader(_videoStream!);
            DexStreamHeader header = reader.ReadStreamHeaderAsync(token).AsTask().GetAwaiter().GetResult();

            _streamWidth = header.Width;
            _streamHeight = header.Height;

            _pipeline = D3D11VideoPipeline.Create(
                WindowHandle,
                header,
                _pendingClientWidth,
                _pendingClientHeight,
                _loggerFactory.CreateLogger<D3D11VideoPipeline>());
            _pipeline.Scaling = _scaling;
            DecoderName = _pipeline.DecoderName;
            _viewport = _pipeline.Viewport;

            Report(
                SessionState.Streaming,
                $"Streaming {header.Width}x{header.Height} at up to {header.RefreshRateHz:0.#} Hz.",
                $"{header.DeviceName} · {header.Codec} · {_pipeline.DecoderName}");

            RunPacketLoop(reader, token);
        }
        catch (Exception ex) when (ex is OperationCanceledException || token.IsCancellationRequested)
        {
            Report(SessionState.Stopped, "Streaming stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The video loop failed.");
            Report(SessionState.Failed, Describe(ex), ex.ToString());
        }
        finally
        {
            _pipeline?.Dispose();
            _pipeline = null;
        }
    }

    private void RunPacketLoop(DexVideoStreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            DexPacket? next = reader.ReadPacketAsync(token).AsTask().GetAwaiter().GetResult();
            if (next is not { } packet)
            {
                Report(SessionState.Stopped, "The device stopped sending video.");
                return;
            }

            ApplyPendingResize();

            switch (packet.Header.Type)
            {
                case DexPacketType.Config:
                case DexPacketType.Frame:
                    PresentPacket(packet);
                    break;

                case DexPacketType.DisplayChanged:
                    HandleDisplayChanged(packet);
                    break;

                case DexPacketType.Heartbeat:
                    // Proof the device is alive with an idle desktop; nothing to draw.
                    break;

                case DexPacketType.Clipboard:
                    OnDeviceClipboard(Encoding.UTF8.GetString(packet.Payload.Span));
                    break;

                case DexPacketType.Log:
                    AgentLog?.Invoke(Encoding.UTF8.GetString(packet.Payload.Span));
                    break;
            }
        }
    }

    private void PresentPacket(DexPacket packet)
    {
        long arrival = _metrics.NowUs;
        bool isConfig = packet.Header.IsCodecConfig;

        _metrics.OnPacketReceived(packet.Header.PayloadLength, packet.Header.IsKeyFrame);

        bool presented = _pipeline!.Submit(
            packet.Payload.Span,
            (long)packet.Header.PresentationTimeUs,
            isConfig,
            packet.Header.IsKeyFrame);

        if (presented)
        {
            long? captureHostTime = _clock.HasEstimate
                ? _clock.ToHostTimeUs((long)packet.Header.PresentationTimeUs)
                : null;
            _metrics.OnFramePresented(captureHostTime, arrival);
            _viewport = _pipeline.Viewport;
        }
        else if (!isConfig)
        {
            _metrics.OnFrameDropped();
        }
    }

    private void HandleDisplayChanged(DexPacket packet)
    {
        DexStreamHeader header = DexStreamHeader.Parse(packet.Payload.Span);
        _streamWidth = header.Width;
        _streamHeight = header.Height;
        _pipeline!.UpdateSourceGeometry(header);
        _viewport = _pipeline.Viewport;

        Report(
            SessionState.Streaming,
            $"Streaming {header.Width}x{header.Height} at up to {header.RefreshRateHz:0.#} Hz.",
            $"{header.DeviceName} · {header.Codec} · {_pipeline.DecoderName}");
    }

    private void ApplyPendingResize()
    {
        int width = _pendingClientWidth;
        int height = _pendingClientHeight;

        if (_pipeline is null || (width == _pipeline.ClientWidth && height == _pipeline.ClientHeight))
        {
            return;
        }

        _pipeline.Resize(width, height);
        _viewport = _pipeline.Viewport;
    }

    /// <summary>Tells the session the window's client area changed size, in physical pixels.</summary>
    public void SetClientSize(int width, int height)
    {
        _pendingClientWidth = Math.Max(width, 1);
        _pendingClientHeight = Math.Max(height, 1);
    }

    private async Task RunPingLoopAsync(CancellationToken token)
    {
        TimeSpan interval = _options.PingInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : _options.PingInterval;

        using var timer = new PeriodicTimer(interval);

        try
        {
            // One immediate ping so the clock estimate exists before the first frames are measured.
            await _control!.SendPingAsync(_metrics.NowUs, token).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _control!.SendPingAsync(_metrics.NowUs, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The session is shutting down.
        }
    }

    private void OnPong(DexPong pong)
    {
        _clock.Add(pong);
        _metrics.OnControlRoundTrip(pong.RoundTripUs);
    }

    /// <summary>Raised when the device's clipboard changed, so the host can mirror it.</summary>
    public event Action<string>? DeviceClipboardChanged;

    private void OnDeviceClipboard(string text) => DeviceClipboardChanged?.Invoke(text);

    // --- Input -----------------------------------------------------------------------------------

    /// <summary>Sends a key event, mapping the Win32 virtual key to an Android keycode.</summary>
    public Task SendKeyAsync(int virtualKey, bool down, AndroidMetaState metaState, int repeat = 0)
    {
        int keyCode = WindowsKeyMap.ToAndroidKeyCode(virtualKey);
        if (keyCode == AndroidKeyCodes.Unknown || _control is null)
        {
            return Task.CompletedTask;
        }

        return Guard(_control.SendKeyEventAsync(
            down ? DexKeyAction.Down : DexKeyAction.Up, keyCode, (int)metaState, repeat, _shutdown.Token));
    }

    /// <summary>Sends text for the device to type, so the device's own layout and IME apply.</summary>
    public Task SendTextAsync(string text)
        => _control is null || text.Length == 0
            ? Task.CompletedTask
            : Guard(_control.SendTextAsync(text, _shutdown.Token));

    /// <summary>
    /// Sends a pointer event. Coordinates are in window client pixels and are mapped through the
    /// current viewport, so a click lands where the user aimed regardless of letterboxing or scaling.
    /// </summary>
    public Task SendPointerAsync(
        DexPointerAction action,
        double clientX,
        double clientY,
        DexPointerButtons buttons,
        DexPointerButtons actionButton)
    {
        if (_control is null)
        {
            return Task.CompletedTask;
        }

        Viewport viewport = _viewport;
        if (viewport.IsEmpty)
        {
            return Task.CompletedTask;
        }

        DevicePoint point = viewport.MapToDevice(clientX, clientY);
        float pressure = action is DexPointerAction.Up or DexPointerAction.Cancel ? 0f : 1f;

        return Guard(_control.SendPointerEventAsync(
            action,
            pointerId: -1,
            point.X,
            point.Y,
            viewport.SourceWidth,
            viewport.SourceHeight,
            pressure,
            actionButton,
            buttons,
            _shutdown.Token));
    }

    /// <summary>Sends a scroll event at a window position.</summary>
    public Task SendScrollAsync(
        double clientX,
        double clientY,
        float horizontal,
        float vertical,
        DexPointerButtons buttons)
    {
        if (_control is null)
        {
            return Task.CompletedTask;
        }

        Viewport viewport = _viewport;
        if (viewport.IsEmpty)
        {
            return Task.CompletedTask;
        }

        DevicePoint point = viewport.MapToDevice(clientX, clientY);
        return Guard(_control.SendScrollAsync(
            point.X, point.Y, viewport.SourceWidth, viewport.SourceHeight,
            horizontal, vertical, buttons, _shutdown.Token));
    }

    public Task SendSystemActionAsync(DexSystemAction action)
        => _control is null ? Task.CompletedTask : Guard(_control.SendSystemActionAsync(action, _shutdown.Token));

    public Task SetDeviceClipboardAsync(string text, bool paste)
        => _control is null ? Task.CompletedTask : Guard(_control.SetClipboardAsync(text, paste, _shutdown.Token));

    /// <summary>Asks the encoder for a key frame, used to recover after a decoder reset.</summary>
    public Task RequestKeyFrameAsync()
        => _control is null ? Task.CompletedTask : Guard(_control.RequestKeyFrameAsync(_shutdown.Token));

    public Task SetBitrateAsync(int bitsPerSecond)
        => _control is null ? Task.CompletedTask : Guard(_control.SetBitrateAsync(bitsPerSecond, _shutdown.Token));

    /// <summary>
    /// Swallows the failures that are normal at shutdown, so input handlers never surface an
    /// exception to the UI thread for a session that is already closing.
    /// </summary>
    private async Task Guard(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "A control message was dropped because the session is closing.");
        }
    }

    private void OnConnectionFaulted(Exception error)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        Report(SessionState.Failed, Describe(error), error.ToString());
    }

    private void Report(SessionState state, string message, string? detail = null)
    {
        Status = new SessionStatus(state, message, detail);
        StatusChanged?.Invoke(Status);
    }

    /// <summary>Turns an exception into a sentence a user can act on.</summary>
    internal static string Describe(Exception error) => error switch
    {
        AdbAuthorizationException => "The phone did not authorize this computer for USB debugging. " +
            "Unlock the screen, reconnect the cable, and tap Allow.",
        UsbDeviceException usb => usb.Message,
        AdbProtocolException adb => adb.Message,
        DexProtocolException dex => dex.Message,
        MediaPipelineException media => media.Message,
        FileNotFoundException missing => missing.Message,
        TimeoutException timeout => timeout.Message,
        _ => $"{error.GetType().Name}: {error.Message}",
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Best effort: let the agent restore the phone's screen and exit cleanly before the transport
        // goes away, otherwise the panel can be left switched off.
        if (_control is not null)
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (_options.TurnPhoneScreenOff)
                {
                    await _control.SetDisplayPowerAsync(true, stopTimeout.Token).ConfigureAwait(false);
                }

                await _control.ShutdownAgentAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stop the agent cleanly.");
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_videoThread is not null && !_videoThread.Join(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("The video thread did not stop within 3 seconds.");
        }

        foreach (Task? task in new[] { _controlReplyLoop, _pingLoop, _agentLogLoop })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown races are expected.
            }
        }

        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        _videoStream?.Dispose();
        _agentShell?.Dispose();

        if (_connection is not null)
        {
            _connection.Faulted -= OnConnectionFaulted;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        else if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        Report(SessionState.Stopped, "Session closed.");
    }
}
