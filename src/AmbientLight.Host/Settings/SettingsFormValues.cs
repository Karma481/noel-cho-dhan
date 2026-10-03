using AmbientLight.Core.Settings;

namespace AmbientLight.Host.Settings;

/// <summary>
/// The settings the Settings window edits, in the units it shows them (percentages, frames per second).
/// Converting through this record keeps the window free of settings arithmetic and guarantees that every
/// setting the window does not show survives an edit unchanged.
/// </summary>
/// <param name="OverlayEnabled">Glow overlay on/off.</param>
/// <param name="OverlayBrightnessPercent">Glow color intensity, 0..100.</param>
/// <param name="BlurRadiusPercent">Glow fade distance as a percentage of the shorter screen side, 0..25.</param>
/// <param name="SpreadWidthPercent">Solid band thickness as a percentage of the shorter screen side, 0.5..25.</param>
/// <param name="OpacityPercent">Peak glow opacity, 0..100.</param>
/// <param name="TargetFps">Capture rate cap.</param>
/// <param name="PauseOverlayInExclusiveFullscreen">Suspend the overlay while an exclusive-fullscreen game runs.</param>
/// <param name="PreferPowerSavingGpu">Ask Windows to run the app on the integrated GPU of a hybrid laptop.</param>
/// <param name="SerialEnabled">LED strip output on/off.</param>
/// <param name="PortName">Serial port of the LED controller.</param>
/// <param name="BaudRate">Serial baud rate.</param>
public sealed record SettingsFormValues(
    bool OverlayEnabled,
    double OverlayBrightnessPercent,
    double BlurRadiusPercent,
    double SpreadWidthPercent,
    double OpacityPercent,
    int TargetFps,
    bool PauseOverlayInExclusiveFullscreen,
    bool PreferPowerSavingGpu,
    bool SerialEnabled,
    string PortName,
    int BaudRate)
{
    /// <summary>Battery-friendly capture rate offered by the window.</summary>
    public const int PowerSavingFps = 30;

    /// <summary>Smooth capture rate offered by the window (the default).</summary>
    public const int SmoothFps = 60;

    /// <summary>Slider range of <see cref="OverlayBrightnessPercent"/> and <see cref="OpacityPercent"/>.</summary>
    public const double MaxPercent = 100;

    /// <summary>Slider minimum of <see cref="SpreadWidthPercent"/> (0.005 as a fraction).</summary>
    public const double MinSpreadPercent = 0.5;

    /// <summary>Slider maximum of <see cref="SpreadWidthPercent"/> and <see cref="BlurRadiusPercent"/> (0.25 as a fraction).</summary>
    public const double MaxGeometryPercent = 25;

    /// <summary>Reads the form values from <paramref name="settings"/>.</summary>
    public static SettingsFormValues From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsFormValues(
            settings.Overlay.Enabled,
            ToPercent(settings.Overlay.Brightness),
            ToPercent(settings.Overlay.BlurRadiusFraction),
            ToPercent(settings.Overlay.SpreadFraction),
            ToPercent(settings.Overlay.Opacity),
            settings.Capture.MaxFps,
            settings.Performance.PauseOverlayInExclusiveFullscreen,
            settings.Performance.PreferPowerSavingGpu,
            settings.Serial.Enabled,
            settings.Serial.PortName,
            settings.Serial.BaudRate);
    }

    /// <summary>
    /// Returns <paramref name="settings"/> with the form values applied. Percentages are clamped to the slider
    /// ranges, so a value can only fail validation for a reason the user can fix (such as a missing COM port).
    /// A value the form shows unchanged keeps its exact stored value, so the one-decimal rounding of the
    /// percentages never rewrites settings the user did not touch.
    /// </summary>
    public AppSettings ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var shown = From(settings);
        var overlay = settings.Overlay;
        return settings with
        {
            Overlay = overlay with
            {
                Enabled = OverlayEnabled,
                Brightness = Merge(OverlayBrightnessPercent, shown.OverlayBrightnessPercent, overlay.Brightness, 0, MaxPercent),
                BlurRadiusFraction = Merge(BlurRadiusPercent, shown.BlurRadiusPercent, overlay.BlurRadiusFraction, 0, MaxGeometryPercent),
                SpreadFraction = Merge(SpreadWidthPercent, shown.SpreadWidthPercent, overlay.SpreadFraction, MinSpreadPercent, MaxGeometryPercent),
                Opacity = Merge(OpacityPercent, shown.OpacityPercent, overlay.Opacity, 0, MaxPercent),
            },
            Capture = settings.Capture with { MaxFps = TargetFps },
            Performance = settings.Performance with
            {
                PauseOverlayInExclusiveFullscreen = PauseOverlayInExclusiveFullscreen,
                PreferPowerSavingGpu = PreferPowerSavingGpu,
            },
            Serial = settings.Serial with
            {
                Enabled = SerialEnabled,
                PortName = PortName.Trim(),
                BaudRate = BaudRate,
            },
        };
    }

    private static float Merge(double percent, double shownPercent, float stored, double min, double max)
    {
        if (percent.Equals(shownPercent))
        {
            return stored;
        }

        var clamped = double.IsFinite(percent) ? Math.Clamp(percent, min, max) : min;
        return (float)(clamped / 100.0);
    }

    private static double ToPercent(float fraction) => Math.Round(fraction * 100.0, 1, MidpointRounding.AwayFromZero);
}
