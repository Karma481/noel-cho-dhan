using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AmbientLight.Core.Color;

/// <summary>
/// An 8-bit-per-channel color in R, G, B byte order.
/// </summary>
/// <remarks>
/// The layout is packed to exactly 3 bytes so a <c>Span&lt;ColorRgb&gt;</c> can be reinterpreted with
/// <see cref="MemoryMarshal.AsBytes{T}(Span{T})"/> and written to the serial port as an Adalight
/// payload without any per-LED copy or conversion.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct ColorRgb(byte R, byte G, byte B)
{
    /// <summary>Size of one packed color in bytes.</summary>
    public const int SizeInBytes = 3;

    /// <summary>All channels off.</summary>
    public static ColorRgb Black => default;

    /// <summary>All channels at full intensity.</summary>
    public static ColorRgb White => new(byte.MaxValue, byte.MaxValue, byte.MaxValue);

    /// <summary>
    /// Converts a normalized color (each channel nominally 0..1) to bytes, clamping out-of-range
    /// values (for example HDR scRGB values above 1) and rounding to nearest.
    /// </summary>
    public static ColorRgb FromNormalized(Vector3 normalized)
    {
        var scaled = Vector3.Clamp(normalized, Vector3.Zero, Vector3.One) * byte.MaxValue;
        return new ColorRgb(
            (byte)MathF.Round(scaled.X, MidpointRounding.AwayFromZero),
            (byte)MathF.Round(scaled.Y, MidpointRounding.AwayFromZero),
            (byte)MathF.Round(scaled.Z, MidpointRounding.AwayFromZero));
    }

    /// <summary>Returns the channels mapped to 0..1.</summary>
    public Vector3 ToNormalized() => new Vector3(R, G, B) / byte.MaxValue;

    /// <summary>Linear interpolation in 8-bit space; <paramref name="t"/> is clamped to 0..1.</summary>
    public static ColorRgb Lerp(ColorRgb from, ColorRgb to, float t)
    {
        var amount = Math.Clamp(t, 0f, 1f);
        return FromNormalized(Vector3.Lerp(from.ToNormalized(), to.ToNormalized(), amount));
    }

    /// <summary>Multiplies every channel by <paramref name="factor"/> (clamped to 0..1).</summary>
    public ColorRgb Scaled(float factor)
    {
        var amount = Math.Clamp(factor, 0f, 1f);
        return FromNormalized(ToNormalized() * amount);
    }

    /// <summary>
    /// Integer Rec.709 luma approximation (0..255) using weights 54/183/19 out of 256.
    /// </summary>
    public byte Luma => (byte)(((R * 54) + (G * 183) + (B * 19)) >> 8);

    /// <summary>Sum of the three channels, used for LED current estimation.</summary>
    public int ChannelSum => R + G + B;

    /// <summary>Formats the color as <c>#RRGGBB</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
}
