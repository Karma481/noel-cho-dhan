using System.Numerics;
using AmbientLight.Processing.Color;

namespace AmbientLight.Processing.Tests;

public sealed class ColorTemperatureTests
{
    [Fact]
    public void Neutral_IsExactlyUnity()
    {
        Assert.Equal(Vector3.One, ColorTemperature.GainsFor(ColorTemperature.NeutralKelvin));
    }

    [Fact]
    public void PlanckianLocus_MatchesReferenceChromaticity()
    {
        // Published Planckian locus coordinates (CIE 1931 2°).
        var xy6500 = ColorTemperature.PlanckianChromaticity(6500);
        Assert.Equal(0.3135f, xy6500.X, precision: 3);
        Assert.Equal(0.3237f, xy6500.Y, precision: 3);

        // CIE illuminant A. Kim et al.'s cubic spline is an approximation; its error near 2856 K is ~5e-4.
        var xy2856 = ColorTemperature.PlanckianChromaticity(2856);
        Assert.InRange(xy2856.X, 0.4476f - 0.001f, 0.4476f + 0.001f);
        Assert.InRange(xy2856.Y, 0.4074f - 0.001f, 0.4074f + 0.001f);
    }

    [Fact]
    public void WarmTemperatures_KeepRed_AndCutBlueMoreThanGreen()
    {
        var gains = ColorTemperature.GainsFor(2700);

        Assert.Equal(1f, gains.X, precision: 5);
        Assert.Equal(0.4422f, gains.Y, precision: 3);
        Assert.Equal(0.1007f, gains.Z, precision: 3);
    }

    [Fact]
    public void CoolTemperatures_KeepBlue_AndCutRedMoreThanGreen()
    {
        var gains = ColorTemperature.GainsFor(10000);

        Assert.Equal(1f, gains.Z, precision: 5);
        Assert.Equal(0.7314f, gains.Y, precision: 3);
        Assert.Equal(0.6055f, gains.X, precision: 3);
    }

    [Fact]
    public void Gains_AreWithinUnitRange_AndBlueRisesMonotonicallyWithTemperature()
    {
        var previousBlueToRed = -1f;
        for (var kelvin = ColorTemperature.MinimumKelvin; kelvin <= ColorTemperature.MaximumKelvin; kelvin += 100)
        {
            var gains = ColorTemperature.GainsFor(kelvin);
            Assert.InRange(gains.X, 0f, 1f);
            Assert.InRange(gains.Y, 0f, 1f);
            Assert.InRange(gains.Z, 0f, 1f);
            Assert.Equal(1f, MathF.Max(gains.X, MathF.Max(gains.Y, gains.Z)), precision: 5);

            var blueToRed = gains.Z / gains.X;
            Assert.True(blueToRed >= previousBlueToRed, $"Not monotonic at {kelvin} K");
            previousBlueToRed = blueToRed;
        }
    }

    [Fact]
    public void OutOfRangeTemperatures_AreClamped()
    {
        Assert.Equal(ColorTemperature.GainsFor(ColorTemperature.MinimumKelvin), ColorTemperature.GainsFor(500));
        Assert.Equal(ColorTemperature.GainsFor(ColorTemperature.MaximumKelvin), ColorTemperature.GainsFor(40000));
    }
}
