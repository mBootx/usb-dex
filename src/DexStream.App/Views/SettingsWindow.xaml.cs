using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DexStream.Core.Rendering;
using DexStream.Core.Session;
using DexStream.Core.Settings;

namespace DexStream.App.Views;

/// <summary>The settings dialog. Reads and writes an <see cref="AppSettings"/> record.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        Load(settings);
    }

    /// <summary>The settings as edited. Only meaningful when the dialog returned true.</summary>
    public AppSettings Result { get; private set; }

    private void Load(AppSettings settings)
    {
        SelectByTag(MaxSizeBox, settings.MaxSize.ToString(CultureInfo.InvariantCulture));
        SelectByTag(RefreshRateBox, settings.TargetRefreshRate.ToString(CultureInfo.InvariantCulture));
        SelectByTag(CodecBox, settings.Codec.ToString());
        SelectByTag(BitrateBox, settings.Bitrate.ToString(CultureInfo.InvariantCulture));
        SelectByTag(ScalingBox, settings.Scaling.ToString());

        AutoStartBox.IsChecked = settings.AutoStartOnConnect;
        TurnScreenOffBox.IsChecked = settings.TurnPhoneScreenOff;
        RequireDexBox.IsChecked = settings.RequireDexDisplay;
        ActivateDexBox.IsChecked = settings.ActivateDexIfMissing;
        ReuseAdbKeyBox.IsChecked = settings.ReuseSystemAdbKey;
        AlwaysOnTopBox.IsChecked = settings.AlwaysOnTop;
        MetricsBarBox.IsChecked = settings.ShowMetricsBar;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = new AppSettings
        {
            MaxSize = ReadIntTag(MaxSizeBox),
            TargetRefreshRate = ReadIntTag(RefreshRateBox),
            Bitrate = ReadIntTag(BitrateBox),
            Codec = ReadEnumTag(CodecBox, CodecPreference.Automatic),
            Scaling = ReadEnumTag(ScalingBox, ScalingMode.Fit),
            AutoStartOnConnect = AutoStartBox.IsChecked == true,
            TurnPhoneScreenOff = TurnScreenOffBox.IsChecked == true,
            RequireDexDisplay = RequireDexBox.IsChecked == true,
            ActivateDexIfMissing = ActivateDexBox.IsChecked == true,
            ReuseSystemAdbKey = ReuseAdbKeyBox.IsChecked == true,
            AlwaysOnTop = AlwaysOnTopBox.IsChecked == true,
            ShowMetricsBar = MetricsBarBox.IsChecked == true,
        };

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Selects the item whose <c>Tag</c> matches, leaving the first selected if none does.</summary>
    private static void SelectByTag(ComboBox box, string value)
    {
        foreach (object item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private static int ReadIntTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static T ReadEnumTag<T>(ComboBox box, T fallback) where T : struct, Enum
        => box.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out T value)
            ? value
            : fallback;
}
