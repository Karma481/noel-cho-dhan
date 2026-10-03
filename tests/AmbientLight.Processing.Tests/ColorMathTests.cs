using System.Numerics;
using AmbientLight.Processing.Color;

namespace AmbientLight.Processing.Tests;

public sealed class ColorMathTests
{
    [Fact]
    public void Srgb_RoundTripsEveryByte()
    {
        for (var value = 0; value <= 255; value++)
        {
            var linear = ColorMath.SrgbToLinear(value / 255f);
            Assert.Equal(value, ColorMath.LinearToSrgbByte(linear));
        }
    }

    [Fact]
    public void Srgb_MatchesReferencePoints()
    {
        Assert.Equal(0.2140f, ColorMath.SrgbToLinear(0.5f), precision: 4);       // mid-grey encodes 21.4% light
        Assert.Equal(0.7354f, ColorMath.LinearToSrgb(0.5f), precision: 4);
        Assert.Equal(0f, ColorMath.SrgbToLinear(-1f));
        Assert.Equal(1f, ColorMath.LinearToSrgb(2f), precision: 6);
    }

    [Fact]
    public void Saturation_One_IsIdentity_Zero_IsGreyOfEqualLuma()
    {
        var color = new Vector3(0.6f, 0.3f, 0.1f);

        AssertClose(color, ColorMath.AdjustSaturation(color, 1f));

        var grey = ColorMath.AdjustSaturation(color, 0f);
        Assert.Equal(grey.X, grey.Y, precision: 6);
        Assert.Equal(grey.Y, grey.Z, precision: 6);
        Assert.Equal(ColorMath.Luma(color), ColorMath.Luma(grey), precision: 6);
    }

    [Fact]
    public void SaturationBoost_KeepsLuma_AndIncreasesChroma()
    {
        var color = new Vector3(0.5f, 0.4f, 0.3f);

        var boosted = ColorMath.AdjustSaturation(color, 1.5f);

        Assert.Equal(ColorMath.Luma(color), ColorMath.Luma(boosted), precision: 5);
        Assert.True(boosted.X - boosted.Z > color.X - color.Z);
    }

    [Fact]
    public void SaturationBoost_NeverProducesNegativeChannels_ForGamutEdgeColors()
    {
        // Pure-ish red: blue is already 0, any naive boost would push it negative.
        var red = new Vector3(0.8f, 0.05f, 0f);

        var boosted = ColorMath.AdjustSaturation(red, 2f);

        Assert.True(boosted.X >= 0f && boosted.Y >= 0f && boosted.Z >= 0f, boosted.ToString());
        Assert.Equal(ColorMath.Luma(red), ColorMath.Luma(boosted), precision: 5);
    }

    [Fact]
    public void Saturation_OfBlack_IsBlack()
    {
        Assert.Equal(Vector3.Zero, ColorMath.AdjustSaturation(Vector3.Zero, 2f));
    }

    [Fact]
    public void FitToUnitRange_ScalesTogether_PreservingRatios()
    {
        var fitted = ColorMath.FitToUnitRange(new Vector3(2f, 1f, 0.5f));

        AssertClose(new Vector3(1f, 0.5f, 0.25f), fitted);
        AssertClose(new Vector3(0.2f, 0.1f, 0f), ColorMath.FitToUnitRange(new Vector3(0.2f, 0.1f, -0.3f)));
    }

    [Fact]
    public void LedByte_WithGamma22_IsCloseToLinearLight()
    {
        // sRGB-encode then ^2.2 nearly cancels: LED duty tracks screen light.
        foreach (var linear in new[] { 0.05f, 0.2f, 0.5f, 0.8f, 1f })
        {
            var duty = ColorMath.LinearToLedByte(linear, 2.2f);
            Assert.InRange(duty, (int)(linear * 255) - 4, (int)(linear * 255) + 4);
        }
    }

    [Fact]
    public void LedByte_WithGammaOne_EqualsSrgbEncoding_AndHigherGammaDarkensMidtones()
    {
        Assert.Equal(ColorMath.LinearToSrgbByte(0.3f), ColorMath.LinearToLedByte(0.3f, 1f));
        Assert.True(ColorMath.LinearToLedByte(0.3f, 2.8f) < ColorMath.LinearToLedByte(0.3f, 2.2f));
        Assert.Equal(255, ColorMath.LinearToLedByte(1f, 2.8f));
        Assert.Equal(0, ColorMath.LinearToLedByte(0f, 2.8f));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 5);
        Assert.Equal(expected.Y, actual.Y, precision: 5);
        Assert.Equal(expected.Z, actual.Z, precision: 5);
    }
}
