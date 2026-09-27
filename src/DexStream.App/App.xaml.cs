using System.IO;
using System.Windows;
using System.Windows.Threading;
using DexStream.App.Services;
using DexStream.App.ViewModels;
using DexStream.App.Views;
using DexStream.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DexStream.App;

/// <summary>Application entry point: builds logging, the coordinator and the main window.</summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private MainViewModel? _viewModel;
    private FileLoggerProvider? _fileLogger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception on the UI thread would otherwise take the process down with a
        // Windows error dialog and no explanation of what the app was doing.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _loggerFactory?.CreateLogger<App>().LogCritical(
                args.ExceptionObject as Exception, "Unhandled exception on a background thread.");

        _fileLogger = new FileLoggerProvider(LogFilePath);
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(_fileLogger);
        });

        var coordinator = new DeviceCoordinator(_loggerFactory);
        var settingsStore = new SettingsStore(SettingsStore.DefaultPath);

        _viewModel = new MainViewModel(
            coordinator, settingsStore, _loggerFactory.CreateLogger<MainViewModel>());

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        // Started after the window exists so the first device event has somewhere to be displayed.
        coordinator.Start();
    }

    /// <summary>Where the rolling diagnostic log is written.</summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DexStream",
        "dexstream.log");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggerFactory?.CreateLogger<App>().LogCritical(e.Exception, "Unhandled exception on the UI thread.");

        MessageBox.Show(
            $"DexStream hit an unexpected error:\n\n{e.Exception.Message}\n\n" +
            $"The full details are in {LogFilePath}.",
            "DexStream",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keeping the app alive is better than losing the diagnostics pane the user needs to report
        // the problem.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _loggerFactory?.Dispose();
        _fileLogger?.Dispose();
        base.OnExit(e);
    }
}
