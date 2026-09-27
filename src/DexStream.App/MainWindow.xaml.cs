using System.Windows;
using System.Windows.Controls;
using DexStream.App.Services;
using DexStream.App.ViewModels;
using DexStream.App.Views;

namespace DexStream.App;

/// <summary>
/// The main window: toolbar, stream surface, metrics bar and the diagnostics pane.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        DataContext = viewModel;

        // The view model starts a session against the surface's HWND, which only exists once the
        // element has been added to a window and laid out.
        viewModel.SurfaceProvider = () =>
        {
            (int width, int height) = Surface.PixelSize;
            return (Surface.SurfaceHandle, width, height);
        };

        viewModel.SessionChanged += OnSessionChanged;

        Surface.AttachInput(this);
        Surface.SizeChanged += OnSurfaceSizeChanged;

        LogPathText.Text = App.LogFilePath;

        Loaded += (_, _) => Surface.Focus();
        DataContextChanged += (_, _) => ApplyAlwaysOnTop();
        ApplyAlwaysOnTop();
    }

    private void OnSessionChanged(DexStreamSession? session)
    {
        Surface.Session = session;

        // The placeholder text is only readable while the surface has nothing on it; the hosted window
        // covers it as soon as the first frame is presented.
        Placeholder.Visibility = session is null ? Visibility.Visible : Visibility.Collapsed;

        if (session is not null)
        {
            (int width, int height) = Surface.PixelSize;
            session.SetClientSize(width, height);
            session.Scaling = _viewModel.Settings.Scaling;
        }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Surface.Session is { } session)
        {
            (int width, int height) = Surface.PixelSize;
            session.SetClientSize(width, height);
        }
    }

    /// <summary>
    /// Re-reports the surface size when the window moves to a monitor with a different scale factor.
    /// </summary>
    /// <remarks>
    /// Dragging the window between a 100% and a 150% monitor changes its size in physical pixels while
    /// leaving its size in device-independent units alone, so SizeChanged does not fire. Without this,
    /// the swap chain would keep the old pixel size and the image would be rescaled by the compositor.
    /// </remarks>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        if (Surface.Session is { } session)
        {
            (int width, int height) = Surface.PixelSize;
            session.SetClientSize(width, height);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.Settings) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.ApplySettings(dialog.Result);
            ApplyAlwaysOnTop();
        }
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        => DiagnosticsPane.Visibility = DiagnosticsPane.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void ApplyAlwaysOnTop() => Topmost = _viewModel.Settings.AlwaysOnTop;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SessionChanged -= OnSessionChanged;
        Surface.SizeChanged -= OnSurfaceSizeChanged;
        Surface.Session = null;
        base.OnClosed(e);
    }
}
