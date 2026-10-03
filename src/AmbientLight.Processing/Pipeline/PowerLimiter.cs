using AmbientLight.Core.Color;

namespace AmbientLight.Processing.Pipeline;

/// <summary>Electrical model of the strip used by <see cref="PowerLimiter"/>.</summary>
/// <param name="MaxMilliamps">Budget for the whole strip; 0 disables limiting.</param>
/// <param name="MilliampsPerChannel">Current of one channel at full duty (WS2812B ≈ 20 mA).</param>
/// <param name="IdleMilliampsPerLed">Quiescent current per LED, drawn even when dark.</param>
public readonly record struct PowerBudget(int MaxMilliamps, int MilliampsPerChannel, int IdleMilliampsPerLed)
{
    /// <summary>True when a budget is set.</summary>
    public bool IsEnabled => MaxMilliamps > 0;
}

/// <summary>Outcome of one limiting pass.</summary>
/// <param name="RequestedMilliamps">Estimated current of the frame before limiting.</param>
/// <param name="DeliveredMilliamps">Estimated current after limiting (≤ budget whenever possible).</param>
/// <param name="Scale">Factor applied to every channel (1 = untouched).</param>
public readonly record struct PowerLimitResult(int RequestedMilliamps, int DeliveredMilliamps, float Scale)
{
    /// <summary>True when the frame had to be dimmed.</summary>
    public bool WasLimited => Scale < 1f;
}

/// <summary>
/// Keeps the estimated strip current within a budget by dimming all LEDs by the same factor.
/// </summary>
/// <remarks>
/// <para>
/// An addressable LED draws current roughly proportional to its PWM duty per channel, so the frame's
/// current is <c>idle * n + (ΣR + ΣG + ΣB) / 255 * mAPerChannel</c>. When that exceeds the budget, every
/// channel is multiplied by the factor that brings the colored part back to <c>budget − idle * n</c>.
/// Scaling all channels together keeps every hue intact.
/// </para>
/// <para>
/// Channels are rounded <b>down</b> after scaling, so quantization can only lower the current: the
/// result is guaranteed not to exceed the budget. The limiter is deliberately instantaneous (no
/// smoothing): it is a safety device protecting a USB port or power supply, not an aesthetic effect.
/// </para>
/// </remarks>
public static class PowerLimiter
{
    /// <summary>Estimated current in milliamps for <paramref name="leds"/>.</summary>
    public static int EstimateMilliamps(ReadOnlySpan<ColorRgb> leds, PowerBudget budget)
    {
        var channelSum = ChannelSum(leds);
        return (leds.Length * budget.IdleMilliampsPerLed) + ColoredMilliamps(channelSum, budget.MilliampsPerChannel);
    }

    /// <summary>Dims <paramref name="leds"/> in place if needed and reports what happened.</summary>
    public static PowerLimitResult Apply(Span<ColorRgb> leds, PowerBudget budget)
    {
        var channelSum = ChannelSum(leds);
        var idle = leds.Length * budget.IdleMilliampsPerLed;
        var requested = idle + ColoredMilliamps(channelSum, budget.MilliampsPerChannel);

        if (!budget.IsEnabled || requested <= budget.MaxMilliamps || channelSum == 0)
        {
            return new PowerLimitResult(requested, requested, 1f);
        }

        var coloredBudget = budget.MaxMilliamps - idle;
        if (coloredBudget <= 0)
        {
            leds.Clear();
            return new PowerLimitResult(requested, idle, 0f);
        }

        // scale = coloredBudget / (channelSum * mA / 255), applied as exact integer math:
        // channel' = floor(channel * coloredBudget * 255 / (channelSum * mA)). A float scale could round up
        // by one ulp and push a channel across an integer boundary, breaking the budget guarantee.
        var numerator = (long)coloredBudget * byte.MaxValue;
        var denominator = channelSum * budget.MilliampsPerChannel;

        long limitedSum = 0;
        for (var i = 0; i < leds.Length; i++)
        {
            var led = leds[i];
            var limited = new ColorRgb(
                (byte)(led.R * numerator / denominator),
                (byte)(led.G * numerator / denominator),
                (byte)(led.B * numerator / denominator));
            leds[i] = limited;
            limitedSum += limited.ChannelSum;
        }

        var scale = (float)((double)numerator / denominator);
        return new PowerLimitResult(requested, idle + ColoredMilliamps(limitedSum, budget.MilliampsPerChannel), scale);
    }

    private static long ChannelSum(ReadOnlySpan<ColorRgb> leds)
    {
        long sum = 0;
        foreach (var led in leds)
        {
            sum += led.ChannelSum;
        }

        return sum;
    }

    // Rounded up so the estimate never understates the load.
    private static int ColoredMilliamps(long channelSum, int milliampsPerChannel) =>
        (int)(((channelSum * milliampsPerChannel) + byte.MaxValue - 1) / byte.MaxValue);
}
