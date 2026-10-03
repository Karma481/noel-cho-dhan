using System.Numerics;
using System.Runtime.CompilerServices;

namespace AmbientLight.Processing.Color;

/// <summary>Allocation-free color math on linear-light <see cref="Vector3"/> values (BT.709 primaries).</summary>
public static class ColorMath
{
    /// <summary>Rec.709 / sRGB luminance weights for linear RGB.</summary>
    public static readonly Vector3 Rec709Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Relative luminance of a linear color.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luma(Vector3 linear) => Vector3.Dot(linear, Rec709Luma);

    /// <summary>IEC 61966-2-1 sRGB EOTF: encoded 0..1 to linear 0..1.</summary>
    public static float SrgbToLinear(float encoded)
    {
        var c = Math.Clamp(encoded, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>IEC 61966-2-1 sRGB OETF: linear 0..1 to encoded 0..1.</summary>
    public static float LinearToSrgb(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
    }

    /// <summary>
    /// Scales saturation around the color's own luminance: <c>Y + s * (c - Y)</c>. Linear in the color,
    /// so it commutes with averaging and smoothing. When a boost would push a channel below zero (a color
    /// that is already at the gamut edge) the boost is reduced for that color just enough to keep every
    /// channel non-negative, preserving its hue instead of clipping it.
    /// </summary>
    public static Vector3 AdjustSaturation(Vector3 linear, float saturation)
    {
        var luma = Luma(linear);
        if (luma <= 0f)
        {
            return Vector3.Zero;
        }

        var chroma = linear - new Vector3(luma);
        var effective = saturation;
        var minimum = MathF.Min(linear.X, MathF.Min(linear.Y, linear.Z));
        if (saturation > 1f && minimum < luma)
        {
            // Largest s keeping the smallest channel >= 0: luma + s * (minimum - luma) >= 0.
            effective = MathF.Min(saturation, luma / (luma - minimum));
        }

        return Vector3.Max(new Vector3(luma) + (chroma * effective), Vector3.Zero);
    }

    /// <summary>
    /// Brings a color with a channel above 1 back into range by scaling all channels together, which keeps
    /// hue and saturation (a per-channel clamp would turn saturated highlights white).
    /// </summary>
    public static Vector3 FitToUnitRange(Vector3 linear)
    {
        var clamped = Vector3.Max(linear, Vector3.Zero);
        var peak = MathF.Max(clamped.X, MathF.Max(clamped.Y, clamped.Z));
        return peak > 1f ? clamped / peak : clamped;
    }

    /// <summary>Converts linear 0..1 to an sRGB-encoded byte, rounding to nearest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToSrgbByte(float linear) => (byte)((LinearToSrgb(linear) * 255f) + 0.5f);

    /// <summary>
    /// Converts linear 0..1 to an LED PWM byte. The value is sRGB-encoded first and then raised to
    /// <paramref name="ledGamma"/>, which is exactly what applying a conventional "LED gamma" table to an
    /// sRGB color does. With the default 2.2 the two curves nearly cancel and the LED emits light
    /// proportional to the screen's; higher values darken mid-tones, lower values brighten them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToLedByte(float linear, float ledGamma) =>
        (byte)((MathF.Pow(LinearToSrgb(linear), ledGamma) * 255f) + 0.5f);
}
