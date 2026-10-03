using System.Windows;

namespace AmbientLight.App.Settings;

/// <summary>The Settings window: three tabs (Virtual Overlay, Performance &amp; Mode, Hardware LED) and a live status footer.</summary>
internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
