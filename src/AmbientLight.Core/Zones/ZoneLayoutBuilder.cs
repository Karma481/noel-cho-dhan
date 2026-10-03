using System.Collections.Immutable;
using AmbientLight.Core.Settings;

namespace AmbientLight.Core.Zones;

/// <summary>Builds the zone list for an LED strip mounted around the screen.</summary>
public static class ZoneLayoutBuilder
{
    /// <summary>
    /// Returns one zone per LED, ordered as the LEDs appear along the strip (wire order),
    /// with <see cref="ZoneConfig.Index"/> equal to the position in the returned array.
    /// </summary>
    /// <remarks>
    /// Zones are first generated clockwise starting at the top-left corner
    /// (top: left to right, right: top to bottom, bottom: right to left, left: bottom to top),
    /// then rotated and, for counter-clockwise strips, reversed so that element 0 is the LED at
    /// <see cref="LedLayoutSettings.StartCorner"/>. Corner regions are shared by the two adjacent
    /// edges, which matches how a strip physically wraps a corner.
    /// </remarks>
    public static ImmutableArray<ZoneConfig> Build(LedLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var total = layout.TotalLedCount;
        if (total <= 0)
        {
            return [];
        }

        var depth = layout.SampleDepth;
        var clockwise = new (ScreenEdge Edge, NormalizedRect Region)[total];
        var cursor = 0;

        for (var i = 0; i < layout.TopCount; i++)
        {
            var width = 1f / layout.TopCount;
            clockwise[cursor++] = (ScreenEdge.Top, new NormalizedRect(i * width, 0f, width, depth));
        }

        for (var i = 0; i < layout.RightCount; i++)
        {
            var height = 1f / layout.RightCount;
            clockwise[cursor++] = (ScreenEdge.Right, new NormalizedRect(1f - depth, i * height, depth, height));
        }

        for (var i = 0; i < layout.BottomCount; i++)
        {
            var width = 1f / layout.BottomCount;
            var slot = layout.BottomCount - 1 - i;
            clockwise[cursor++] = (ScreenEdge.Bottom, new NormalizedRect(slot * width, 1f - depth, width, depth));
        }

        for (var i = 0; i < layout.LeftCount; i++)
        {
            var height = 1f / layout.LeftCount;
            var slot = layout.LeftCount - 1 - i;
            clockwise[cursor++] = (ScreenEdge.Left, new NormalizedRect(0f, slot * height, depth, height));
        }

        var offset = layout.StartCorner switch
        {
            StripStartCorner.TopLeft => 0,
            StripStartCorner.TopRight => layout.TopCount,
            StripStartCorner.BottomRight => layout.TopCount + layout.RightCount,
            StripStartCorner.BottomLeft => layout.TopCount + layout.RightCount + layout.BottomCount,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.StartCorner, "Unknown start corner."),
        };

        var builder = ImmutableArray.CreateBuilder<ZoneConfig>(total);
        for (var i = 0; i < total; i++)
        {
            var source = layout.Direction switch
            {
                StripDirection.Clockwise => (offset + i) % total,
                StripDirection.CounterClockwise => ((offset - 1 - i) % total + total) % total,
                _ => throw new ArgumentOutOfRangeException(nameof(layout), layout.Direction, "Unknown direction."),
            };

            var (edge, region) = clockwise[source];
            builder.Add(new ZoneConfig(i, edge, region));
        }

        return builder.MoveToImmutable();
    }
}
