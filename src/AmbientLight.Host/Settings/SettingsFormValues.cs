using AmbientLight.Core.Settings;

namespace AmbientLight.Host.Settings;

/// <summary>
/// The settings the Settings window edits, in the units it shows them (percentages, multipliers, frames per
/// second). Converting through this record keeps the window free of settings arithmetic and guarantees that every
/// setting the window does not show survives an edit unchanged.
/// </summary>
public sealed record SettingsFormValues
{
    /// <summary>Battery-friendly capture rate offered by the window.</summary>
    public const int PowerSavingFps = 30;

    /// <summary>Smooth capture rate offered by the window (the default).</summary>
    public const int SmoothFps = 60;

    /// <summary>Upper bound of the plain percentage sliders (brightness, opacity, layer intensities).</summary>
    public const double MaxPercent = 100;

    /// <summary>Slider minimum of <see cref="InnerGlowPercent"/> (0.002 as a fraction).</summary>
    public const double MinInnerGlowPercent = 0.2;

    /// <summary>Slider maximum of <see cref="InnerGlowPercent"/> (0.1 as a fraction).</summary>
    public const double MaxInnerGlowPercent = 10;

    /// <summary>Slider minimum of <see cref="SpreadWidthPercent"/> (0.005 as a fraction).</summary>
    public const double MinSpreadPercent = 0.5;

    /// <summary>Slider maximum of <see cref="SpreadWidthPercent"/>: half the shorter side, where opposite bands meet.</summary>
    public const double MaxSpreadPercent = 50;

    /// <summary>Slider maximum of <see cref="BlurRadiusPercent"/> (0.5 as a fraction).</summary>
    public const double MaxBlurPercent = 50;

    /// <summary>Slider minimum of <see cref="Saturation"/> (greyscale).</summary>
    public const double MinSaturation = 0;

    /// <summary>Slider maximum of <see cref="Saturation"/>.</summary>
    public const double MaxSaturation = 2;

    /// <summary>Slider minimum of <see cref="Contrast"/> and <see cref="LuminanceGain"/>.</summary>
    public const double MinCurve = 0.5;

    /// <summary>Slider maximum of <see cref="Contrast"/> and <see cref="LuminanceGain"/>.</summary>
    public const double MaxCurve = 2;

    // ── Virtual Overlay ──

    /// <summary>Glow overlay on/off.</summary>
    public bool OverlayEnabled { get; init; }

    /// <summary>Inner glow strength, 0..100.</summary>
    public double InnerIntensityPercent { get; init; }

    /// <summary>Inner glow width as a percentage of the shorter screen side, 0.2..10.</summary>
    public double InnerGlowPercent { get; init; }

    /// <summary>Ambient wash strength, 0..100.</summary>
    public double WashIntensityPercent { get; init; }

    /// <summary>Ambient wash band thickness as a percentage of the shorter screen side, 0.5..50.</summary>
    public double SpreadWidthPercent { get; init; }

    /// <summary>Ambient wash fade distance as a percentage of the shorter screen side, 0..50.</summary>
    public double BlurRadiusPercent { get; init; }

    // ── Color & Blend ──

    /// <summary>How the glow is composited over the screen.</summary>
    public OverlayBlendMode BlendMode { get; init; }

    /// <summary>Glow color intensity, 0..100.</summary>
    public double OverlayBrightnessPercent { get; init; }

    /// <summary>Overall glow strength, 0..100.</summary>
    public double OpacityPercent { get; init; }

    /// <summary>Overlay saturation multiplier, 0..2.</summary>
    public double Saturation { get; init; }

    /// <summary>Overlay contrast curve, 0.5..2.</summary>
    public double Contrast { get; init; }

    /// <summary>Overlay luminance gain, 0.5..2.</summary>
    public double LuminanceGain { get; init; }

    /// <summary>Keep the glow out of the picture of letterboxed video.</summary>
    public bool KeepPictureClear { get; init; }

    // ── Performance & Mode ──

    /// <summary>Capture rate cap.</summary>
    public int TargetFps { get; init; }

    /// <summary>Suspend the overlay while an exclusive-fullscreen game runs.</summary>
    public bool PauseOverlayInExclusiveFullscreen { get; init; }

    /// <summary>Ask Windows to run the app on the integrated GPU of a hybrid laptop.</summary>
    public bool PreferPowerSavingGpu { get; init; }

    // ── Hardware LED ──

    /// <summary>LED strip output on/off.</summary>
    public bool SerialEnabled { get; init; }

    /// <summary>Serial port of the LED controller.</summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>Serial baud rate.</summary>
    public int BaudRate { get; init; }

    /// <summary>Reads the form values from <paramref name="settings"/>.</summary>
    public static SettingsFormValues From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var overlay = settings.Overlay;
        return new SettingsFormValues
        {
            OverlayEnabled = overlay.Enabled,
            InnerIntensityPercent = ToPercent(overlay.InnerIntensity),
            InnerGlowPercent = ToPercent(overlay.InnerGlowFraction),
            WashIntensityPercent = ToPercent(overlay.WashIntensity),
            SpreadWidthPercent = ToPercent(overlay.SpreadFraction),
            BlurRadiusPercent = ToPercent(overlay.BlurRadiusFraction),
            BlendMode = overlay.BlendMode,
            OverlayBrightnessPercent = ToPercent(overlay.Brightness),
            OpacityPercent = ToPercent(overlay.Opacity),
            Saturation = ToMultiplier(overlay.Saturation),
            Contrast = ToMultiplier(overlay.Contrast),
            LuminanceGain = ToMultiplier(overlay.LuminanceGain),
            KeepPictureClear = overlay.KeepPictureClear,
            TargetFps = settings.Capture.MaxFps,
            PauseOverlayInExclusiveFullscreen = settings.Performance.PauseOverlayInExclusiveFullscreen,
            PreferPowerSavingGpu = settings.Performance.PreferPowerSavingGpu,
            SerialEnabled = settings.Serial.Enabled,
            PortName = settings.Serial.PortName,
            BaudRate = settings.Serial.BaudRate,
        };
    }

    /// <summary>
    /// Returns <paramref name="settings"/> with the form values applied. Values are clamped to the slider ranges,
    /// so a value can only fail validation for a reason the user can fix (such as a missing COM port). A value the
    /// form shows unchanged keeps its exact stored value, so the display rounding never rewrites settings the
    /// user did not touch.
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
                InnerIntensity = MergePercent(InnerIntensityPercent, shown.InnerIntensityPercent, overlay.InnerIntensity, 0, MaxPercent),
                InnerGlowFraction = MergePercent(InnerGlowPercent, shown.InnerGlowPercent, overlay.InnerGlowFraction, MinInnerGlowPercent, MaxInnerGlowPercent),
                WashIntensity = MergePercent(WashIntensityPercent, shown.WashIntensityPercent, overlay.WashIntensity, 0, MaxPercent),
                SpreadFraction = MergePercent(SpreadWidthPercent, shown.SpreadWidthPercent, overlay.SpreadFraction, MinSpreadPercent, MaxSpreadPercent),
                BlurRadiusFraction = MergePercent(BlurRadiusPercent, shown.BlurRadiusPercent, overlay.BlurRadiusFraction, 0, MaxBlurPercent),
                BlendMode = Enum.IsDefined(BlendMode) ? BlendMode : overlay.BlendMode,
                Brightness = MergePercent(OverlayBrightnessPercent, shown.OverlayBrightnessPercent, overlay.Brightness, 0, MaxPercent),
                Opacity = MergePercent(OpacityPercent, shown.OpacityPercent, overlay.Opacity, 0, MaxPercent),
                Saturation = Merge(Saturation, shown.Saturation, overlay.Saturation, MinSaturation, MaxSaturation),
                Contrast = Merge(Contrast, shown.Contrast, overlay.Contrast, MinCurve, MaxCurve),
                LuminanceGain = Merge(LuminanceGain, shown.LuminanceGain, overlay.LuminanceGain, MinCurve, MaxCurve),
                KeepPictureClear = KeepPictureClear,
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

    private static float MergePercent(double percent, double shownPercent, float stored, double min, double max) =>
        Merge(percent / 100.0, shownPercent / 100.0, stored, min / 100.0, max / 100.0);

    private static float Merge(double value, double shownValue, float stored, double min, double max)
    {
        if (value.Equals(shownValue))
        {
            return stored;
        }

        return (float)(double.IsFinite(value) ? Math.Clamp(value, min, max) : min);
    }

    private static double ToPercent(float fraction) => Math.Round(fraction * 100.0, 1, MidpointRounding.AwayFromZero);

    private static double ToMultiplier(float value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
