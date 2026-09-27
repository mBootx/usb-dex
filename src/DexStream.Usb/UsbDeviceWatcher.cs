using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.Usb;

/// <summary>
/// Watches the USB bus for ADB interfaces appearing and disappearing.
/// </summary>
/// <remarks>
/// Implemented by polling SetupAPI rather than by subscribing to <c>WM_DEVICECHANGE</c>. Polling
/// needs no window handle and no message loop, which keeps the watcher usable from a background
/// service or a console host as well as from the WPF app, and a SetupAPI enumeration of one
/// interface class costs well under a millisecond. The default interval is a compromise between
/// how quickly a freshly plugged cable is noticed and how little work runs while idle.
/// </remarks>
public sealed class UsbDeviceWatcher : IAsyncDisposable
{
    private readonly AdbUsbDeviceEnumerator _enumerator;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _interval;
    private readonly bool _samsungOnly;

    private Dictionary<string, UsbDeviceInfo> _known = [];
    private Task? _loop;
    private bool _disposed;

    public UsbDeviceWatcher(
        AdbUsbDeviceEnumerator? enumerator = null,
        TimeSpan? pollInterval = null,
        bool samsungOnly = false,
        ILogger<UsbDeviceWatcher>? logger = null)
    {
        _enumerator = enumerator ?? new AdbUsbDeviceEnumerator();
        _interval = pollInterval ?? TimeSpan.FromSeconds(1);
        _samsungOnly = samsungOnly;
        _logger = logger ?? NullLogger<UsbDeviceWatcher>.Instance;
    }

    /// <summary>Raised when an ADB interface appears.</summary>
    public event Action<UsbDeviceInfo>? DeviceConnected;

    /// <summary>Raised when a previously seen ADB interface goes away.</summary>
    public event Action<UsbDeviceInfo>? DeviceDisconnected;

    /// <summary>Devices currently present.</summary>
    public IReadOnlyCollection<UsbDeviceInfo> CurrentDevices => _known.Values;

    /// <summary>
    /// Starts watching. Devices already plugged in are reported through
    /// <see cref="DeviceConnected"/> before this returns, so a caller does not need a separate
    /// initial scan.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            return;
        }

        Poll();
        _loop = Task.Run(() => LoopAsync(_shutdown.Token), CancellationToken.None);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Poll();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Poll()
    {
        Dictionary<string, UsbDeviceInfo> current;
        try
        {
            current = _enumerator
                .Enumerate(_samsungOnly)
                .ToDictionary(d => d.DevicePath, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // A transient SetupAPI failure must not kill the watcher; the next tick retries.
            _logger.LogWarning(ex, "USB enumeration failed; will retry.");
            return;
        }

        Dictionary<string, UsbDeviceInfo> previous = _known;
        _known = current;

        foreach ((string path, UsbDeviceInfo device) in current)
        {
            if (!previous.ContainsKey(path))
            {
                _logger.LogInformation("USB device connected: {Device}", device);
                DeviceConnected?.Invoke(device);
            }
        }

        foreach ((string path, UsbDeviceInfo device) in previous)
        {
            if (!current.ContainsKey(path))
            {
                _logger.LogInformation("USB device disconnected: {Device}", device);
                DeviceDisconnected?.Invoke(device);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // Shutdown races are not interesting.
            }
        }

        _shutdown.Dispose();
    }
}
