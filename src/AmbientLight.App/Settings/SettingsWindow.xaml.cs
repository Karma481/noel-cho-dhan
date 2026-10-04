using System.Windows;

namespace AmbientLight.App.Settings;

/// <summary>
/// The Settings window: four tabs (Virtual Overlay, Color &amp; Blend, Performance &amp; Mode, Hardware LED) and
/// a live status footer.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // The window opens sized to the first and tallest tab; keep that size so switching tabs does not make it
        // jump. A page that does not fit (small screens, extra status lines) scrolls instead.
        ContentRendered -= OnContentRendered;
        SizeToContent = SizeToContent.Manual;
    }
}
