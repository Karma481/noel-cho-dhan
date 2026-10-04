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

    [Fact]
    public void ToneCurve_IsTheIdentity_AtNeutralSettings()
    {
        foreach (var color in new[] { new Vector3(0.6f, 0.3f, 0.1f), new Vector3(0.01f, 0.02f, 0.005f), Vector3.One })
        {
            AssertClose(color, ColorMath.ApplyToneCurve(color, contrast: 1f, gain: 1f));
        }
    }

    [Fact]
    public void ToneCurve_Contrast_DeepensDarks_AndBrightensHighlights()
    {
        var dark = new Vector3(0.02f, 0.01f, 0.03f);
        var bright = new Vector3(0.5f, 0.2f, 0.6f);

        var deeper = ColorMath.ApplyToneCurve(dark, contrast: 1.4f, gain: 1f);
        var brighter = ColorMath.ApplyToneCurve(bright, contrast: 1.4f, gain: 1f);

        Assert.True(deeper.Z < dark.Z, $"{deeper} is not darker than {dark}.");
        Assert.True(brighter.Z > bright.Z, $"{brighter} is not brighter than {bright}.");
    }

    [Fact]
    public void ToneCurve_KeepsHue_AndNeverLeavesTheUnitRange()
    {
        var neonBlue = new Vector3(0.05f, 0.1f, 0.9f);

        var result = ColorMath.ApplyToneCurve(neonBlue, contrast: 2f, gain: 2f);

        // Same channel ratios, so the same hue and saturation; the brightest channel saturates at 1.
        Assert.Equal(1f, result.Z, precision: 5);
        Assert.Equal(neonBlue.X / neonBlue.Z, result.X / result.Z, precision: 4);
        Assert.Equal(neonBlue.Y / neonBlue.Z, result.Y / result.Z, precision: 4);
    }

    [Fact]
    public void ToneCurve_JudgesBrightnessByTheLargestChannel_SoSaturatedBluesStayBright()
    {
        // Pure blue has a luminance of 7%: a luminance-based contrast curve would push it into the shadows.
        var blue = new Vector3(0f, 0f, 0.8f);

        var result = ColorMath.ApplyToneCurve(blue, contrast: 1.4f, gain: 1f);

        Assert.True(result.Z >= blue.Z, $"{result} was darkened.");
    }

    [Fact]
    public void ToneCurve_IsMonotonic()
    {
        var previous = -1f;
        for (var value = 0f; value <= 1f; value += 0.01f)
        {
            var result = ColorMath.ApplyToneCurve(new Vector3(value), contrast: 1.35f, gain: 1.25f).X;

            // Float rounding where the curve reaches 1 can differ by one ulp between neighbours.
            Assert.True(result >= previous - 1e-6f, $"Curve decreases at {value}.");
            previous = result;
        }
    }

    [Fact]
    public void ToneCurve_OfBlack_IsBlack()
    {
        Assert.Equal(Vector3.Zero, ColorMath.ApplyToneCurve(Vector3.Zero, 1.5f, 2f));
    }
}
