using System.Numerics;
using AmbientLight.Core.Color;
using AmbientLight.Processing.Pipeline;

namespace AmbientLight.Processing.Tests;

public sealed class TemporalSmootherAndPowerTests
{
    [Fact]
    public void Smoother_FirstAdvance_JumpsToTarget()
    {
        var smoother = new TemporalSmoother(4);
        Vector3[] target = [new(1f, 0.5f, 0f)];

        Assert.False(smoother.Advance(target, 0.016f, 0.08f));
        Assert.Equal(target[0], smoother.State[0]);
    }

    [Fact]
    public void Smoother_AfterOneTimeConstant_HasCovered63Percent()
    {
        var smoother = new TemporalSmoother(1);
        smoother.Reset([Vector3.Zero]);

        Assert.True(smoother.Advance([Vector3.One], 0.08f, 0.08f));

        Assert.Equal(1f - MathF.Exp(-1f), smoother.State[0].X, precision: 5);
    }

    [Fact]
    public void Smoother_IsFrameRateIndependent()
    {
        var fast = new TemporalSmoother(1);
        var slow = new TemporalSmoother(1);
        fast.Reset([Vector3.Zero]);
        slow.Reset([Vector3.Zero]);
        Vector3[] target = [new(1f, 0.5f, 0.25f)];

        for (var i = 0; i < 10; i++)
        {
            fast.Advance(target, 0.01f, 0.08f);  // 100 Hz
        }

        slow.Advance(target, 0.1f, 0.08f);       // 10 Hz, same total time

        Assert.Equal(slow.State[0].X, fast.State[0].X, precision: 5);
        Assert.Equal(slow.State[0].Z, fast.State[0].Z, precision: 5);
    }

    [Fact]
    public void Smoother_Converges_AndSnapsExactlyToTarget()
    {
        var smoother = new TemporalSmoother(1);
        smoother.Reset([Vector3.Zero]);
        Vector3[] target = [new(0.7f, 0.2f, 0.9f)];

        var steps = 0;
        while (smoother.Advance(target, 0.016f, 0.08f))
        {
            steps++;
            Assert.True(steps < 200, "Smoothing never converged.");
        }

        Assert.Equal(target[0], smoother.State[0]);
    }

    [Fact]
    public void Smoother_ZeroTimeConstant_DisablesSmoothing()
    {
        var smoother = new TemporalSmoother(1);
        smoother.Reset([Vector3.Zero]);

        Assert.False(smoother.Advance([Vector3.One], 0.016f, 0f));
        Assert.Equal(Vector3.One, smoother.State[0]);
    }

    [Fact]
    public void Power_Disabled_OrUnderBudget_LeavesColorsUntouched()
    {
        ColorRgb[] leds = [ColorRgb.White, ColorRgb.White];

        var disabled = PowerLimiter.Apply(leds, new PowerBudget(0, 20, 1));
        var underBudget = PowerLimiter.Apply(leds, new PowerBudget(1000, 20, 1));

        Assert.False(disabled.WasLimited);
        Assert.False(underBudget.WasLimited);
        Assert.Equal(122, underBudget.RequestedMilliamps); // 2 * 1 mA idle + 2 * 3 * 20 mA
        Assert.All(leds, led => Assert.Equal(ColorRgb.White, led));
    }

    [Fact]
    public void Power_OverBudget_DimsUniformly_AndStaysWithinBudget()
    {
        var leds = Enumerable.Repeat(new ColorRgb(255, 128, 64), 100).ToArray();
        var budget = new PowerBudget(400, 20, 1);

        var result = PowerLimiter.Apply(leds, budget);

        Assert.True(result.WasLimited);
        Assert.True(result.DeliveredMilliamps <= budget.MaxMilliamps, $"{result.DeliveredMilliamps} mA > budget");
        Assert.Equal(result.DeliveredMilliamps, PowerLimiter.EstimateMilliamps(leds, budget));

        // Hue preserved: channel ratios stay (within rounding) 4 : 2 : 1.
        var led = leds[0];
        Assert.InRange(led.R / (double)led.B, 3.5, 4.6);
        Assert.InRange(led.G / (double)led.B, 1.7, 2.3);
    }

    [Fact]
    public void Power_BudgetBelowIdleCurrent_TurnsTheStripOff()
    {
        var leds = Enumerable.Repeat(ColorRgb.White, 100).ToArray();

        var result = PowerLimiter.Apply(leds, new PowerBudget(80, 20, 1));

        Assert.Equal(0f, result.Scale);
        Assert.All(leds, led => Assert.Equal(ColorRgb.Black, led));
    }

    [Fact]
    public void Power_NeverExceedsBudget_ForRandomFrames()
    {
        var random = new Random(1234);
        var leds = new ColorRgb[300];
        for (var trial = 0; trial < 2000; trial++)
        {
            for (var i = 0; i < leds.Length; i++)
            {
                leds[i] = new ColorRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }

            var budget = new PowerBudget(random.Next(301, 20_000), random.Next(5, 61), random.Next(0, 2));
            var result = PowerLimiter.Apply(leds, budget);

            Assert.True(result.DeliveredMilliamps <= budget.MaxMilliamps, $"Trial {trial}: {result} with {budget}");
            Assert.Equal(result.DeliveredMilliamps, PowerLimiter.EstimateMilliamps(leds, budget));
        }
    }
}
