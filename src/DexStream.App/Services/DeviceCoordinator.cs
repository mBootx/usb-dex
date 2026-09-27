using DexStream.Core.Session;
using DexStream.Core.Settings;
using DexStream.Usb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.App.Services;

/// <summary>
/// Watches USB for Galaxy devices and owns the lifetime of the streaming session.
/// </summary>
/// <remarks>
/// Reconnection is the behaviour that matters here. A cable pulled mid-stream, or a phone that
/// reboots, must leave the app in a clean state rather than a half-torn-down one, so device removal
/// always disposes the session even if the session is already failing for its own reasons.
/// </remarks>
public sealed class DeviceCoordinator : IAsyncDisposable
{
    private readonly UsbDeviceWatcher _watcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private DexStreamSession? _session;
    private UsbDeviceInfo? _activeDevice;
    private bool _disposed;

    public DeviceCoordinator(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DeviceCoordinator>();
        _watcher = new UsbDeviceWatcher(
            logger: _loggerFactory.CreateLogger<UsbDeviceWatcher>());
        _watcher.DeviceConnected += OnDeviceConnected;
        _watcher.DeviceDisconnected += OnDeviceDisconnected;
    }

    /// <summary>Raised when the set of connected devices changes.</summary>
    public event Action<IReadOnlyList<UsbDeviceInfo>>? DevicesChanged;

    /// <summary>Raised when the session reports a new status, or when there is no session.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised for each line of device agent output.</summary>
    public event Action<string>? AgentLog;

    /// <summary>Raised when a session starts, so the UI can wire itself to it.</summary>
    public event Action<DexStreamSession?>? SessionChanged;

    /// <summary>Devices currently attached.</summary>
    public IReadOnlyList<UsbDeviceInfo> Devices { get; private set; } = [];

    /// <summary>The live session, or null.</summary>
    public DexStreamSession? Session => _session;

    /// <summary>Starts watching USB. Devices already attached are reported immediately.</summary>
    public void Start() => _watcher.Start();

    /// <summary>
    /// Starts streaming from the first attached device.
    /// </summary>
    /// <param name="settings">The user's preferences, converted to session options.</param>
    /// <param name="windowHandle">The HWND the stream is presented into.</param>
    /// <param name="clientWidth">Client width in physical pixels.</param>
    /// <param name="clientHeight">Client height in physical pixels.</param>
    public async Task StartStreamingAsync(
        AppSettings settings,
        IntPtr windowHandle,
        int clientWidth,
        int clientHeight,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                return;
            }

            UsbDeviceInfo? device = Devices.FirstOrDefault(d => d.IsSamsung) ?? Devices.FirstOrDefault();
            if (device is null)
            {
                Report(new SessionStatus(
                    SessionState.Disconnected,
                    "No device found.",
                    "Connect a Galaxy phone with a USB cable and enable USB debugging in Developer options."));
                return;
            }

            StreamSessionOptions options = settings.ToSessionOptions();
            var session = new DexStreamSession(
                device, options, windowHandle, clientWidth, clientHeight, _loggerFactory);

            session.StatusChanged += Report;
            session.AgentLog += line => AgentLog?.Invoke(line);

            _session = session;
            _activeDevice = device;
            SessionChanged?.Invoke(session);

            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start streaming from {Device}.", device);

                // The session already reported the failure; dispose it so a retry starts clean.
                _session = null;
                _activeDevice = null;
                SessionChanged?.Invoke(null);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>Stops the session, if one is running.</summary>
    public async Task StopStreamingAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DexStreamSession? session = _session;
            _session = null;
            _activeDevice = null;

            if (session is null)
            {
                return;
            }

            SessionChanged?.Invoke(null);
            await session.DisposeAsync().ConfigureAwait(false);

            Report(Devices.Count > 0
                ? new SessionStatus(SessionState.DeviceReady, "Stopped. Device still connected.")
                : new SessionStatus(SessionState.Disconnected, "No device connected."));
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void OnDeviceConnected(UsbDeviceInfo device)
    {
        Devices = _watcher.CurrentDevices.ToArray();
        DevicesChanged?.Invoke(Devices);

        // Nothing can be said about DeX support yet: the model is only readable once ADB is up.
        Report(new SessionStatus(
            SessionState.DeviceReady,
            $"{device.Description} connected.",
            device.IsSamsung ? null : "This is not a Samsung device, so there is no DeX desktop to stream."));
    }

    private void OnDeviceDisconnected(UsbDeviceInfo device)
    {
        Devices = _watcher.CurrentDevices.ToArray();
        DevicesChanged?.Invoke(Devices);

        if (_activeDevice is not null
            && string.Equals(_activeDevice.DevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            Report(new SessionStatus(
                SessionState.Disconnected,
                "The device was disconnected.",
                "Reconnect the cable to start streaming again."));

            // Fire and forget: teardown must not block the watcher's polling thread.
            _ = Task.Run(StopStreamingAsync);
            return;
        }

        if (Devices.Count == 0)
        {
            Report(new SessionStatus(SessionState.Disconnected, "No device connected."));
        }
    }

    private void Report(SessionStatus status) => StatusChanged?.Invoke(status);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _watcher.DeviceConnected -= OnDeviceConnected;
        _watcher.DeviceDisconnected -= OnDeviceDisconnected;

        await StopStreamingAsync().ConfigureAwait(false);
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _sessionGate.Dispose();
    }
}
