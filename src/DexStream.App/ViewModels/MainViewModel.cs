using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DexStream.App.Services;
using DexStream.Core.Device;
using DexStream.Core.Metrics;
using DexStream.Core.Protocol;
using DexStream.Core.Rendering;
using DexStream.Core.Settings;
using DexStream.Usb;
using Microsoft.Extensions.Logging;

namespace DexStream.App.ViewModels;

/// <summary>
/// Drives the main window: connection state, the start and stop commands, and the performance figures.
/// </summary>
/// <remarks>
/// Status and log events arrive from background threads, so everything that touches bound state is
/// marshalled onto the dispatcher. Metrics are polled on a timer rather than pushed, because pushing a
/// property change per frame at 120 fps would put more work on the UI thread than the decode loop
/// does.
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    /// <summary>How often the metrics bar is refreshed. Four times a second reads as live.</summary>
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Lines kept in the diagnostics pane before the oldest are dropped.</summary>
    private const int MaxLogLines = 500;

    private readonly DeviceCoordinator _coordinator;
    private readonly SettingsStore _settingsStore;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _metricsTimer;

    private AppSettings _settings;
    private DexStreamSession? _session;
    private SessionStatus _status = new(SessionState.Disconnected, "Looking for a device...");
    private MetricsSnapshot _metrics = MetricsSnapshot.Empty;
    private bool _busy;
    private bool _disposed;

    public MainViewModel(DeviceCoordinator coordinator, SettingsStore settingsStore, ILogger<MainViewModel> logger)
    {
        _coordinator = coordinator;
        _settingsStore = settingsStore;
        _logger = logger;

        _settings = settingsStore.Load(out string? warning);
        if (warning is not null)
        {
            AppendLog(warning);
        }

        _coordinator.StatusChanged += OnStatusChanged;
        _coordinator.DevicesChanged += OnDevicesChanged;
        _coordinator.AgentLog += OnAgentLog;
        _coordinator.SessionChanged += OnSessionChanged;

        StartCommand = new RelayCommand(async () => await StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => CanStop);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics, () => Log.Count > 0);
        BackCommand = new RelayCommand(() => SendSystemAction(DexSystemAction.Back), () => IsStreaming);
        HomeCommand = new RelayCommand(() => SendSystemAction(DexSystemAction.Home), () => IsStreaming);
        RecentsCommand = new RelayCommand(() => SendSystemAction(DexSystemAction.Recents), () => IsStreaming);

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = MetricsInterval,
        };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        _metricsTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a session starts or stops, so the window can attach the surface.</summary>
    public event Action<DexStreamSession?>? SessionChanged;

    /// <summary>The HWND of the stream surface, supplied by the window once it exists.</summary>
    public Func<(IntPtr Handle, int Width, int Height)>? SurfaceProvider { get; set; }

    public ObservableCollection<string> Log { get; } = [];

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand CopyDiagnosticsCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand HomeCommand { get; }

    public ICommand RecentsCommand { get; }

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            _settings = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMetricsBar));
            OnPropertyChanged(nameof(AlwaysOnTop));
        }
    }

    public bool ShowMetricsBar => _settings.ShowMetricsBar;

    public bool AlwaysOnTop => _settings.AlwaysOnTop;

    public string StatusText => _status.Message;

    public string? StatusDetail => _status.Detail;

    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(_status.Detail);

    public bool IsError => _status.IsError;

    public bool IsStreaming => _status.State == SessionState.Streaming;

    public bool IsBusy => _busy || _status.IsBusy;

    public bool CanStart => !IsBusy && !IsStreaming && _coordinator.Devices.Count > 0;

    public bool CanStop => IsStreaming || _session is not null;

    /// <summary>Line describing the attached device, or the absence of one.</summary>
    public string DeviceText
    {
        get
        {
            UsbDeviceInfo? device = _coordinator.Devices.FirstOrDefault();
            if (device is null)
            {
                return "No device connected";
            }

            string model = _session?.Model ?? device.Description;
            DexSupportInfo support = _session?.Support ?? default;

            return support.Level switch
            {
                DexSupportLevel.Supported => $"{support.MarketingName ?? model} · DeX supported",
                DexSupportLevel.Unknown => $"{model} · DeX support unknown",
                DexSupportLevel.Unsupported => $"{model} · not a DeX device",
                _ => model,
            };
        }
    }

    /// <summary>Which display is being captured and why.</summary>
    public string CaptureText
    {
        get
        {
            if (_session?.CaptureDisplay is not { } display)
            {
                return string.Empty;
            }

            string reason = _session.SelectionReason switch
            {
                DexSelectionReason.NamedDexDisplay => "DeX desktop",
                DexSelectionReason.AgentCreatedDisplay => "DexStream desktop",
                DexSelectionReason.SecondaryDisplay => "secondary display",
                DexSelectionReason.FallbackPrimaryDisplay => "phone screen (DeX not running)",
                _ => "unknown",
            };

            return $"{display.Width}×{display.Height} · {reason}";
        }
    }

    public string DecoderText => _session?.DecoderName ?? string.Empty;

    public string FrameRateText => _metrics.FramesPerSecond.ToString("0.0", CultureInfo.CurrentCulture) + " fps";

    public string BitrateText => _metrics.MegabitsPerSecond.ToString("0.0", CultureInfo.CurrentCulture) + " Mbit/s";

    /// <summary>Median end-to-end latency, which is the figure the target is expressed against.</summary>
    public string LatencyText => _metrics.EndToEndLatencyMs.Count == 0
        ? "—"
        : $"{_metrics.EndToEndLatencyMs.P50:0.0} ms (p95 {_metrics.EndToEndLatencyMs.P95:0.0})";

    public string RoundTripText => _metrics.ControlRoundTripMs <= 0
        ? "—"
        : $"{_metrics.ControlRoundTripMs:0.0} ms";

    public string FrameStatsText =>
        $"{_metrics.FramesPresented:N0} presented · {_metrics.FramesDropped:N0} dropped · " +
        $"{_metrics.KeyFrames:N0} key frames";

    private async Task StartAsync()
    {
        if (SurfaceProvider is null)
        {
            AppendLog("The stream surface is not ready yet.");
            return;
        }

        (IntPtr handle, int width, int height) = SurfaceProvider();
        if (handle == IntPtr.Zero)
        {
            AppendLog("The stream surface has no window handle yet.");
            return;
        }

        _busy = true;
        RaiseCommandState();

        try
        {
            await _coordinator.StartStreamingAsync(_settings, handle, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the session failed.");
            AppendLog(DexStreamSession.Describe(ex));
        }
        finally
        {
            _busy = false;
            RaiseCommandState();
        }
    }

    private async Task StopAsync()
    {
        _busy = true;
        RaiseCommandState();

        try
        {
            await _coordinator.StopStreamingAsync();
        }
        finally
        {
            _busy = false;
            RaiseCommandState();
        }
    }

    /// <summary>Replaces the settings and persists them.</summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save settings.");
            AppendLog($"Settings could not be saved: {ex.Message}");
        }

        if (_session is not null)
        {
            _session.Scaling = settings.Scaling;
        }
    }

    private void SendSystemAction(DexSystemAction action) => _ = _session?.SendSystemActionAsync(action);

    private void OnSessionChanged(DexStreamSession? session)
    {
        Dispatch(() =>
        {
            _session = session;
            SessionChanged?.Invoke(session);
            RaiseCommandState();
            OnPropertyChanged(nameof(DeviceText));
            OnPropertyChanged(nameof(CaptureText));
            OnPropertyChanged(nameof(DecoderText));
        });
    }

    private void OnStatusChanged(SessionStatus status)
    {
        Dispatch(() =>
        {
            _status = status;

            if (status.IsError || status.Detail is { Length: > 0 })
            {
                AppendLog(status.Detail is { Length: > 0 } ? $"{status.Message} — {status.Detail}" : status.Message);
            }

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(HasStatusDetail));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(CaptureText));
            OnPropertyChanged(nameof(DecoderText));
            OnPropertyChanged(nameof(DeviceText));
            RaiseCommandState();
        });
    }

    private void OnDevicesChanged(IReadOnlyList<UsbDeviceInfo> devices)
    {
        Dispatch(() =>
        {
            OnPropertyChanged(nameof(DeviceText));
            RaiseCommandState();

            if (devices.Count > 0 && _settings.AutoStartOnConnect && !IsStreaming && !IsBusy)
            {
                _ = StartAsync();
            }
        });
    }

    private void OnAgentLog(string line) => Dispatch(() => AppendLog(line));

    private void AppendLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");

        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }

        (CopyDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Log));
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that must not take the app down.
            _logger.LogWarning(ex, "Could not copy the diagnostics to the clipboard.");
        }
    }

    private void RefreshMetrics()
    {
        if (_session is null)
        {
            if (_metrics.FramesPresented != 0)
            {
                _metrics = MetricsSnapshot.Empty;
                RaiseMetricsProperties();
            }

            return;
        }

        _metrics = _session.Metrics;
        RaiseMetricsProperties();
    }

    private void RaiseMetricsProperties()
    {
        OnPropertyChanged(nameof(FrameRateText));
        OnPropertyChanged(nameof(BitrateText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(RoundTripText));
        OnPropertyChanged(nameof(FrameStatsText));
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsBusy));
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (HomeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RecentsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Runs an action on the dispatcher, or inline if already on it.</summary>
    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metricsTimer.Stop();

        _coordinator.StatusChanged -= OnStatusChanged;
        _coordinator.DevicesChanged -= OnDevicesChanged;
        _coordinator.AgentLog -= OnAgentLog;
        _coordinator.SessionChanged -= OnSessionChanged;

        await _coordinator.DisposeAsync();
    }
}

/// <summary>A minimal <see cref="ICommand"/>, so the app needs no MVVM framework.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _executeAsync;
    private readonly Action? _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (_execute is not null)
        {
            _execute();
            return;
        }

        // Fire and forget: the command's own body reports failures, and ICommand has no async form.
        _ = _executeAsync!();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
