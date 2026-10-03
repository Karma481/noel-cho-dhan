using AmbientLight.Core.Settings;
using Vortice.DXGI;

namespace AmbientLight.Capture.ColorSpace;

/// <summary>How the pixels of the duplicated desktop surface are encoded. Values are shared with ZoneReduce.hlsl.</summary>
public enum SurfaceEncoding : uint
{
    /// <summary>8-bit UNORM holding sRGB-encoded values; the shader applies the sRGB EOTF.</summary>
    SrgbUnorm = 0,

    /// <summary>FP16 scRGB: linear light, BT.709 primaries, 1.0 = 80 nits, may exceed 1 or be negative.</summary>
    ScRgbLinear = 1,

    /// <summary>An <c>*_SRGB</c> format: the sampler already returns linear 0..1 values.</summary>
    LinearSdr = 2,
}

/// <summary>Shader parameters that turn captured pixels into linear 0..1 LED-range light.</summary>
/// <param name="Encoding">Pixel encoding of the desktop surface.</param>
/// <param name="SdrWhiteScale">scRGB value of SDR reference white; 1 for SDR surfaces.</param>
/// <param name="PeakWhite">Display peak luminance relative to SDR white; 1 means "no headroom, clip".</param>
/// <param name="KneeStart">Relative luminance where the highlight roll-off starts.</param>
/// <param name="ToneMapEnabled">Whether highlights above SDR white are rolled off (true) or clipped.</param>
public readonly record struct ColorMappingParameters(
    SurfaceEncoding Encoding,
    float SdrWhiteScale,
    float PeakWhite,
    float KneeStart,
    bool ToneMapEnabled);

/// <summary>Chooses the duplication formats and the HDR → LED mapping for a monitor.</summary>
public static class ColorSpaceMapping
{
    /// <summary>Luminance in nits that scRGB defines as 1.0.</summary>
    public const float ScRgbReferenceNits = 80f;

    /// <summary>
    /// SDR white assumed when Windows does not report one. It only matters on HDR desktops and only
    /// scales overall brightness; the user-facing brightness setting compensates for it.
    /// </summary>
    public const float FallbackSdrWhiteNits = 200f;

    /// <summary>
    /// Start of the highlight roll-off as a fraction of SDR white. Below it HDR content maps linearly,
    /// so SDR content on an HDR desktop (which tops out at 1.0) is barely touched.
    /// </summary>
    public const float DefaultKneeStart = 0.75f;

    /// <summary>
    /// Formats offered to <c>IDXGIOutput5::DuplicateOutput1</c>, in order of preference.
    /// On an HDR desktop FP16 keeps the full range and precision; on SDR, BGRA8 halves bandwidth.
    /// BGRA8 stays in both lists as a fallback the OS can always satisfy.
    /// </summary>
    public static Format[] PreferredDuplicationFormats(bool isHdr) =>
        isHdr
            ? [Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm]
            : [Format.B8G8R8A8_UNorm];

    /// <summary>Maps the actual format of an acquired surface to its encoding.</summary>
    /// <exception cref="NotSupportedException">The format is not one duplication is documented to produce.</exception>
    public static SurfaceEncoding EncodingOf(Format format) => format switch
    {
        Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm or Format.R8G8B8A8_UNorm => SurfaceEncoding.SrgbUnorm,
        Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8X8_UNorm_SRgb or Format.R8G8B8A8_UNorm_SRgb => SurfaceEncoding.LinearSdr,
        Format.R16G16B16A16_Float => SurfaceEncoding.ScRgbLinear,
        _ => throw new NotSupportedException($"Desktop surface format {format} is not supported."),
    };

    /// <summary>Computes shader parameters for a surface of <paramref name="format"/> captured from <paramref name="output"/>.</summary>
    public static ColorMappingParameters Resolve(Format format, DisplayOutputInfo output, CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        var encoding = EncodingOf(format);
        if (encoding != SurfaceEncoding.ScRgbLinear)
        {
            return new ColorMappingParameters(encoding, SdrWhiteScale: 1f, PeakWhite: 1f, DefaultKneeStart, ToneMapEnabled: false);
        }

        // An FP16 surface from an SDR desktop has SDR white at exactly 1.0 (no SDR boost exists).
        if (!output.IsHdr)
        {
            return new ColorMappingParameters(encoding, SdrWhiteScale: 1f, PeakWhite: 1f, DefaultKneeStart, ToneMapEnabled: false);
        }

        var sdrWhiteNits = float.IsFinite(output.SdrWhiteNits) && output.SdrWhiteNits > 0f
            ? output.SdrWhiteNits
            : FallbackSdrWhiteNits;

        // Unknown or implausible EDID peak (some monitors report 0, or less than SDR white): no headroom.
        var peakWhite = float.IsFinite(output.MaxLuminanceNits) && output.MaxLuminanceNits > sdrWhiteNits
            ? output.MaxLuminanceNits / sdrWhiteNits
            : 1f;

        return new ColorMappingParameters(
            encoding,
            SdrWhiteScale: sdrWhiteNits / ScRgbReferenceNits,
            PeakWhite: peakWhite,
            DefaultKneeStart,
            ToneMapEnabled: settings.HdrToneMapping && peakWhite > 1f);
    }
}
