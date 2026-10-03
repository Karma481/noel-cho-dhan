using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Tests;

public sealed class ZoneLayoutBuilderTests
{
    private static LedLayoutSettings Layout(StripStartCorner corner, StripDirection direction) => new()
    {
        TopCount = 4,
        RightCount = 2,
        BottomCount = 4,
        LeftCount = 2,
        StartCorner = corner,
        Direction = direction,
        SampleDepth = 0.1f,
    };

    [Fact]
    public void Build_ProducesOneZonePerLed_WithSequentialIndices()
    {
        var zones = ZoneLayoutBuilder.Build(Layout(StripStartCorner.BottomLeft, StripDirection.Clockwise));

        Assert.Equal(12, zones.Length);
        for (var i = 0; i < zones.Length; i++)
        {
            Assert.Equal(i, zones[i].Index);
            Assert.True(zones[i].Region.IsValid, $"Zone {i} region {zones[i].Region} is outside the unit square.");
        }
    }

    [Fact]
    public void Clockwise_FromTopLeft_WalksTopRightBottomLeft()
    {
        var zones = ZoneLayoutBuilder.Build(Layout(StripStartCorner.TopLeft, StripDirection.Clockwise));

        ScreenEdge[] expected =
        [
            ScreenEdge.Top, ScreenEdge.Top, ScreenEdge.Top, ScreenEdge.Top,
            ScreenEdge.Right, ScreenEdge.Right,
            ScreenEdge.Bottom, ScreenEdge.Bottom, ScreenEdge.Bottom, ScreenEdge.Bottom,
            ScreenEdge.Left, ScreenEdge.Left,
        ];
        Assert.Equal(expected, zones.Select(zone => zone.Edge));

        Assert.Equal(0f, zones[0].Region.X);
        Assert.Equal(0.75f, zones[3].Region.X);
        Assert.Equal(0f, zones[4].Region.Y);
        Assert.Equal(0.5f, zones[5].Region.Y);
        Assert.Equal(0.75f, zones[6].Region.X);
        Assert.Equal(0f, zones[9].Region.X);
        Assert.Equal(0.5f, zones[10].Region.Y);
        Assert.Equal(0f, zones[11].Region.Y);
    }

    [Fact]
    public void Clockwise_FromBottomLeft_StartsUpTheLeftEdge()
    {
        var zones = ZoneLayoutBuilder.Build(Layout(StripStartCorner.BottomLeft, StripDirection.Clockwise));

        Assert.Equal(ScreenEdge.Left, zones[0].Edge);
        Assert.Equal(0.5f, zones[0].Region.Y);
        Assert.Equal(ScreenEdge.Left, zones[1].Edge);
        Assert.Equal(0f, zones[1].Region.Y);
        Assert.Equal(ScreenEdge.Top, zones[2].Edge);
        Assert.Equal(0f, zones[2].Region.X);
    }

    [Fact]
    public void CounterClockwise_FromBottomLeft_RunsAlongTheBottomLeftToRight()
    {
        var zones = ZoneLayoutBuilder.Build(Layout(StripStartCorner.BottomLeft, StripDirection.CounterClockwise));

        Assert.All(zones.Take(4), zone => Assert.Equal(ScreenEdge.Bottom, zone.Edge));
        Assert.Equal([0f, 0.25f, 0.5f, 0.75f], zones.Take(4).Select(zone => zone.Region.X));
        Assert.Equal(ScreenEdge.Right, zones[4].Edge);
        Assert.Equal(0.5f, zones[4].Region.Y);
        Assert.Equal(ScreenEdge.Left, zones[^1].Edge);
        Assert.Equal(0.5f, zones[^1].Region.Y);
    }

    [Theory]
    [InlineData(StripStartCorner.TopLeft)]
    [InlineData(StripStartCorner.TopRight)]
    [InlineData(StripStartCorner.BottomRight)]
    [InlineData(StripStartCorner.BottomLeft)]
    public void CounterClockwise_IsTheReverseOfClockwise_FromTheSameCorner(StripStartCorner corner)
    {
        var clockwise = ZoneLayoutBuilder.Build(Layout(corner, StripDirection.Clockwise));
        var counterClockwise = ZoneLayoutBuilder.Build(Layout(corner, StripDirection.CounterClockwise));

        var clockwiseRegions = clockwise.Select(zone => zone.Region).ToArray();
        var reversed = counterClockwise.Select(zone => zone.Region).Reverse().ToArray();

        Assert.Equal(clockwiseRegions, reversed);
    }

    [Fact]
    public void EdgesWithZeroLeds_AreSkipped()
    {
        var layout = Layout(StripStartCorner.TopLeft, StripDirection.Clockwise) with { BottomCount = 0 };

        var zones = ZoneLayoutBuilder.Build(layout);

        Assert.Equal(8, zones.Length);
        Assert.DoesNotContain(zones, zone => zone.Edge == ScreenEdge.Bottom);
    }

    [Fact]
    public void ToPixels_CoversAtLeastOnePixel_AndStaysInBounds()
    {
        var region = new NormalizedRect(0.999f, 0.999f, 0.001f, 0.001f);

        var (left, top, right, bottom) = region.ToPixels(1920, 1080);

        Assert.InRange(left, 0, 1919);
        Assert.InRange(top, 0, 1079);
        Assert.True(right > left);
        Assert.True(bottom > top);
        Assert.True(right <= 1920);
        Assert.True(bottom <= 1080);
    }
}
