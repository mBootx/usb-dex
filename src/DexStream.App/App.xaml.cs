using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DexStream.App.Services;
using DexStream.App.ViewModels;
using DexStream.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DexStream.App;

/// <summary>Application entry point: builds logging, the coordinator and the main window.</summary>
public partial class App : Application
{
    /// <summary>
    /// Starts the app, waits for the main window to render and run briefly, then exits with 0 — or with
    /// a non-zero code if anything threw.
    /// </summary>
    /// <remarks>
    /// CI runs the published executable this way. Compiling proves nothing about XAML resources,
    /// bindings, text layout or the hosted swap-chain window; this is the check that actually executes
    /// them on Windows before a build reaches anyone.
    /// </remarks>
    public const string SmokeTestSwitch = "--smoke-test";

    private const int ExitStartupFailure = 1;
    private const int ExitSmokeTimeout = 2;
    private const int ExitAgentNotEmbedded = 3;

    /// <summary>How long the smoke test lets the app run after its first render.</summary>
    private static readonly TimeSpan SmokeSettleTime = TimeSpan.FromSeconds(3);

    /// <summary>How long the smoke test waits for a first render before giving up.</summary>
    private static readonly TimeSpan SmokeRenderTimeout = TimeSpan.FromSeconds(60);

    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private MainViewModel? _viewModel;
    private FileLoggerProvider? _fileLogger;
    private Timer? _smokeWatchdog;
    private string? _smokeSettingsPath;
    private bool _smokeTest;
    private volatile bool _startupComplete;
    private int _reportingFailure;
    private int _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _smokeTest = e.Args.Any(arg => string.Equals(arg, SmokeTestSwitch, StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _fileLogger = new FileLoggerProvider(LogFilePath);
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(_fileLogger);
        });
        _logger = _loggerFactory.CreateLogger<App>();
        _logger.LogInformation(
            "DexStream {Version} starting{Mode}.",
            typeof(App).Assembly.GetName().Version,
            _smokeTest ? " in smoke-test mode" : string.Empty);

        if (_smokeTest)
        {
            StartSmokeWatchdog();
        }

        var coordinator = new DeviceCoordinator(_loggerFactory);
        var settingsStore = new SettingsStore(_smokeTest ? CreateSmokeTestSettings() : SettingsStore.DefaultPath);

        _viewModel = new MainViewModel(
            coordinator, settingsStore, _loggerFactory.CreateLogger<MainViewModel>());

        var window = new MainWindow(_viewModel);
        this.MainWindow = window;
        window.ContentRendered += OnMainWindowRendered;
        window.Show();

        // Started after the window exists so the first device event has somewhere to be displayed.
        coordinator.Start();
    }

    /// <summary>Where the rolling diagnostic log is written.</summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DexStream",
        "dexstream.log");

    private void OnMainWindowRendered(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.ContentRendered -= OnMainWindowRendered;
        }

        _startupComplete = true;
        _logger?.LogInformation("Main window rendered.");

        if (_smokeTest)
        {
            RunSmokeChecksThenExit();
        }
    }

    // --- Failure handling --------------------------------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _logger?.LogCritical(e.Exception, "Unhandled exception on the UI thread.");

        if (_smokeTest)
        {
            ExitNow(ExitStartupFailure);
            return;
        }

        // A dialog is already up. Its own handler decides what happens next; stacking another dialog
        // for every follow-on failure is what turned one error into a cascade before.
        if (Volatile.Read(ref _reportingFailure) == 1)
        {
            return;
        }

        ShowFailure(e.Exception);

        // A failure before the window has rendered leaves a half-built UI behind, and a window that
        // cannot draw is worse than a clean exit. After startup, staying alive keeps the diagnostics
        // pane reachable, which is what the user needs to report the problem.
        if (!_startupComplete)
        {
            ExitNow(ExitStartupFailure);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception; the process is exiting.");

        // The process ends as soon as this handler returns and the log writer is asynchronous, so the
        // line explaining why would otherwise be lost with it.
        _fileLogger?.Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>Shows one error dialog, however many failures arrive while it is open.</summary>
    private void ShowFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _reportingFailure, 1) == 1)
        {
            return;
        }

        try
        {
            _fileLogger?.Flush(TimeSpan.FromSeconds(1));

            // A native dialog rather than WPF's MessageBox. WPF's needs WPF's own text layout, so when
            // the failure is in that layout — as it was when globalization data was missing — WPF's
            // dialog throws the same exception again from inside itself.
            MessageBoxW(
                IntPtr.Zero,
                $"DexStream hit an unexpected error:\n\n{error.Message}\n\nThe full details are in {LogFilePath}.",
                "DexStream",
                MB_OK | MB_ICONERROR | MB_TASKMODAL | MB_SETFOREGROUND);
        }
        finally
        {
            Interlocked.Exchange(ref _reportingFailure, 0);
        }
    }

    /// <summary>Flushes the log and shuts down with <paramref name="exitCode"/>, once.</summary>
    private void ExitNow(int exitCode)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }

        _logger?.LogInformation("Exiting with code {ExitCode}.", exitCode);
        _fileLogger?.Flush(TimeSpan.FromSeconds(2));
        Shutdown(exitCode);
    }

    // --- Smoke test --------------------------------------------------------------------------------

    /// <summary>
    /// Writes throwaway settings for the smoke test, so it neither reads nor overwrites the user's own
    /// and cannot start streaming to a phone that happens to be plugged in.
    /// </summary>
    private string CreateSmokeTestSettings()
    {
        _smokeSettingsPath = Path.Combine(
            Path.GetTempPath(), $"dexstream-smoke-{Environment.ProcessId}.json");
        new SettingsStore(_smokeSettingsPath).Save(new AppSettings { AutoStartOnConnect = false });
        return _smokeSettingsPath;
    }

    /// <summary>
    /// Fails the smoke test if the window never renders. A thread-pool timer rather than a dispatcher
    /// one, so it still fires if the UI thread is the thing that is stuck.
    /// </summary>
    private void StartSmokeWatchdog()
    {
        _smokeWatchdog = new Timer(
            _ =>
            {
                if (_startupComplete)
                {
                    return;
                }

                _logger?.LogCritical(
                    "Smoke test failed: the main window did not render within {Seconds} seconds.",
                    SmokeRenderTimeout.TotalSeconds);
                _fileLogger?.Flush(TimeSpan.FromSeconds(2));
                Environment.Exit(ExitSmokeTimeout);
            },
            null,
            SmokeRenderTimeout,
            Timeout.InfiniteTimeSpan);
    }

    private void RunSmokeChecksThenExit()
    {
        // The smoke test validates the deliverable, and a deliverable without the agent inside it would
        // start fine here and then fail the moment a phone is connected.
        try
        {
            (byte[] agent, AgentPackage.Source origin) = AgentPackage.Load(_logger);
            if (origin != AgentPackage.Source.Embedded)
            {
                _logger?.LogCritical(
                    "Smoke test failed: the device agent is not embedded in this build (found it via {Origin}).",
                    origin);
                ExitNow(ExitAgentNotEmbedded);
                return;
            }

            _logger?.LogInformation("Embedded device agent present ({Bytes} bytes).", agent.Length);
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogCritical(ex, "Smoke test failed: this build contains no device agent.");
            ExitNow(ExitAgentNotEmbedded);
            return;
        }

        // Keep running briefly, so work that only starts after the first render — the metrics timer,
        // the first USB poll, deferred bindings — also gets its chance to fail.
        var settle = new DispatcherTimer { Interval = SmokeSettleTime };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            _logger?.LogInformation("Smoke test passed.");
            ExitNow(0);
        };
        settle.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _smokeWatchdog?.Dispose();

        if (_viewModel is not null)
        {
            _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_smokeSettingsPath is not null)
        {
            try
            {
                File.Delete(_smokeSettingsPath);
            }
            catch (IOException)
            {
                // A leftover file in %TEMP% is harmless.
            }
        }

        _loggerFactory?.Dispose();
        _fileLogger?.Dispose();
        base.OnExit(e);
    }

    // --- Win32 -------------------------------------------------------------------------------------

    private const uint MB_OK = 0x0000_0000;
    private const uint MB_ICONERROR = 0x0000_0010;
    private const uint MB_TASKMODAL = 0x0000_2000;
    private const uint MB_SETFOREGROUND = 0x0001_0000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
