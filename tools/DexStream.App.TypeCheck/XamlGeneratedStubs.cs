// Stands in for the members the XAML markup compiler generates for DexStream.App.
//
// The real build gets these from the .xaml files. This file exists only so the app's C# can be
// type-checked without the Windows Desktop SDK; see the csproj for why. Keep it in step with the
// x:Name'd elements in the XAML: a name here that the XAML no longer has is harmless, but a name in
// the XAML that is missing here makes this project fail while the real build succeeds.
using System.Windows.Controls;
using DexStream.App.Views;

namespace DexStream.App;

public partial class App
{
    public void InitializeComponent()
    {
    }
}

public partial class MainWindow
{
    internal StreamSurface Surface = null!;
    internal StackPanel Placeholder = null!;
    internal Border DiagnosticsPane = null!;
    internal TextBlock LogPathText = null!;
    internal Button SettingsButton = null!;
    internal Button DiagnosticsButton = null!;

    public void InitializeComponent()
    {
    }
}
