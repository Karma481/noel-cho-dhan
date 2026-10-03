using System.Numerics;

namespace AmbientLight.Processing.Color;

/// <summary>Converts a correlated color temperature to linear-RGB channel gains.</summary>
/// <remarks>
/// The chromaticity of a black body at temperature T follows the Planckian locus, approximated with the
/// cubic splines of Kim et al. (2002, valid 1667 K – 25000 K). That xy point is converted to XYZ at unit
/// luminance and then to linear sRGB. Gains are taken relative to 6500 K, so 6500 K is exactly neutral, and
/// normalized so the largest gain is 1: changing the temperature never boosts a channel (which would clip)
/// and only removes light from the others.
/// </remarks>
public static class ColorTemperature
{
    /// <summary>Temperature that produces unity gains.</summary>
    public const int NeutralKelvin = 6500;

    /// <summary>Lowest supported temperature (candle-like).</summary>
    public const int MinimumKelvin = 1900;

    /// <summary>Highest supported temperature (blue sky).</summary>
    public const int MaximumKelvin = 12000;

    private static readonly Vector3 NeutralRgb = PlanckianLinearRgb(NeutralKelvin);

    /// <summary>Channel gains in 0..1 for <paramref name="kelvin"/> (clamped to the supported range).</summary>
    public static Vector3 GainsFor(int kelvin)
    {
        var clamped = Math.Clamp(kelvin, MinimumKelvin, MaximumKelvin);
        if (clamped == NeutralKelvin)
        {
            return Vector3.One;
        }

        var relative = Vector3.Max(PlanckianLinearRgb(clamped) / NeutralRgb, Vector3.Zero);
        var peak = MathF.Max(relative.X, MathF.Max(relative.Y, relative.Z));
        return relative / peak;
    }

    /// <summary>CIE 1931 xy chromaticity of a black body at <paramref name="kelvin"/> (Kim et al.).</summary>
    public static Vector2 PlanckianChromaticity(int kelvin)
    {
        double t = Math.Clamp(kelvin, 1667, 25000);
        var t2 = t * t;
        var t3 = t2 * t;

        var x = t <= 4000
            ? (-0.2661239e9 / t3) - (0.2343589e6 / t2) + (0.8776956e3 / t) + 0.179910
            : (-3.0258469e9 / t3) + (2.1070379e6 / t2) + (0.2226347e3 / t) + 0.240390;

        var x2 = x * x;
        var x3 = x2 * x;
        var y = t switch
        {
            <= 2222 => (-1.1063814 * x3) - (1.34811020 * x2) + (2.18555832 * x) - 0.20219683,
            <= 4000 => (-0.9549476 * x3) - (1.37418593 * x2) + (2.09137015 * x) - 0.16748867,
            _ => (3.0817580 * x3) - (5.87338670 * x2) + (3.75112997 * x) - 0.37001483,
        };

        return new Vector2((float)x, (float)y);
    }

    private static Vector3 PlanckianLinearRgb(int kelvin)
    {
        var xy = PlanckianChromaticity(kelvin);
        double x = xy.X;
        double y = xy.Y;

        // XYZ at unit luminance.
        var bigX = x / y;
        const double BigY = 1.0;
        var bigZ = (1.0 - x - y) / y;

        // XYZ (D65) to linear sRGB, IEC 61966-2-1.
        var r = (3.2404542 * bigX) - (1.5371385 * BigY) - (0.4985314 * bigZ);
        var g = (-0.9692660 * bigX) + (1.8760108 * BigY) + (0.0415560 * bigZ);
        var b = (0.0556434 * bigX) - (0.2040259 * BigY) + (1.0572252 * bigZ);
        return new Vector3((float)r, (float)g, (float)b);
    }
}
