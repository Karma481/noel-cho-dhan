using System.Numerics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Processing.Color;
using AmbientLight.Processing.Letterbox;

namespace AmbientLight.Processing.Pipeline;

/// <summary>
/// Everything the per-frame color math needs, precomputed from settings once per settings version so the
/// hot loop does no lookups, no conversions and no allocations.
/// </summary>
/// <param name="TemperatureGains">Color temperature as linear RGB gains (applies to LEDs and overlay).</param>
/// <param name="Saturation">Saturation multiplier of the LED colors.</param>
/// <param name="DisplaySaturation">Saturation multiplier of the overlay colors.</param>
/// <param name="DisplayContrast">Contrast of the overlay tone curve (1 = linear).</param>
/// <param name="DisplayGain">Luminance gain of the overlay tone curve (1 = unchanged).</param>
/// <param name="LedGains">Strip white balance times brightness (LEDs only).</param>
/// <param name="LedGamma">Exponent applied to sRGB-encoded values for LED PWM.</param>
/// <param name="BlackThresholdLinear">Linear luma below which a zone is forced to black.</param>
/// <param name="SmoothingSeconds">Time constant of the temporal smoothing; 0 disables it.</param>
/// <param name="Power">Strip current budget.</param>
/// <param name="LetterboxEnabled">Whether letterbox detection runs.</param>
/// <param name="Letterbox">Letterbox detection thresholds.</param>
public readonly record struct ColorPipelineParameters(
    Vector3 TemperatureGains,
    float Saturation,
    float DisplaySaturation,
    float DisplayContrast,
    float DisplayGain,
    Vector3 LedGains,
    float LedGamma,
    float BlackThresholdLinear,
    float SmoothingSeconds,
    PowerBudget Power,
    bool LetterboxEnabled,
    LetterboxParameters Letterbox)
{
    /// <summary>Derives the parameters from validated settings.</summary>
    public static ColorPipelineParameters From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var processing = settings.Processing;
        var serial = settings.Serial;
        var overlay = settings.Overlay;
        var whiteBalance = processing.WhiteBalance;

        return new ColorPipelineParameters(
            ColorTemperature.GainsFor(processing.ColorTemperatureK),
            processing.Saturation,
            overlay.Saturation,
            overlay.Contrast,
            overlay.LuminanceGain,
            new Vector3(whiteBalance.R, whiteBalance.G, whiteBalance.B) * processing.Brightness,
            processing.LedGamma,
            ColorMath.SrgbToLinear(processing.BlackThreshold / 255f),
            processing.SmoothingTimeMs / 1000f,
            new PowerBudget(serial.MaxCurrentMilliamps, serial.MilliampsPerChannel, serial.IdleMilliampsPerLed),
            settings.Letterbox.Enabled,
            LetterboxParameters.From(settings.Letterbox, ZoneSampleFrame.ProfileResolution));
    }
}
