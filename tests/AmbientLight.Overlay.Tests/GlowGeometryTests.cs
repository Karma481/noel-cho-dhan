using System.Collections.Immutable;
using AmbientLight.Core.Color;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Overlay.Rendering;

namespace AmbientLight.Overlay.Tests;

public sealed class GlowGeometryTests
{
    private static readonly OverlaySettings Defaults = new();

    [Fact]
    public void Layout_4K_RendersAtOneEighth_WithANarrowInnerAndAWideWashLayer()
    {
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);

        Assert.Equal(480, layout.RenderWidth);
        Assert.Equal(270, layout.RenderHeight);
        Assert.Equal(8f, layout.ScaleX);
        Assert.Equal(8f, layout.ScaleY);

        // Inner glow: 3% of the shorter side, blur radius twice the width (radius = 3 sigma).
        Assert.Equal(0.03f * 270f, layout.InnerSpread, precision: 4);
        Assert.Equal(2f * 0.03f * 270f / 3f, layout.InnerSigma, precision: 4);

        // Ambient wash: 20% spread, 25% blur radius.
        Assert.Equal(0.20f * 270f, layout.WashSpread, precision: 4);
        Assert.Equal(0.25f * 270f / 3f, layout.WashSigma, precision: 4);

        // The margin fits the wider blur.
        Assert.Equal((int)MathF.Ceiling(3f * layout.WashSigma) + 1, layout.Margin);
        Assert.Equal(layout.RenderWidth + (2 * layout.Margin), layout.CanvasWidth);
    }

    [Theory]
    [InlineData(1366, 768, 8)]
    [InlineData(2560, 1080, 8)]
    [InlineData(1920, 1080, 1)]
    [InlineData(1080, 1920, 6)]  // portrait
    public void Layout_ScaleAlwaysCoversTheMonitorExactly(int width, int height, int divisor)
    {
        var layout = GlowGeometry.ComputeLayout(width, height, Defaults with { ResolutionDivisor = divisor });

        Assert.Equal(width, layout.RenderWidth * layout.ScaleX, precision: 3);
        Assert.Equal(height, layout.RenderHeight * layout.ScaleY, precision: 3);
        Assert.True(layout.RenderWidth * divisor >= width);
    }

    [Fact]
    public void Layout_ClampsBlursToTheDirect2DLimit()
    {
        // 8K at full resolution with the widest blur: 0.5 * 4320 / 3 = 720 > 250.
        var layout = GlowGeometry.ComputeLayout(7680, 4320, Defaults with { ResolutionDivisor = 1, BlurRadiusFraction = 0.5f });

        Assert.Equal(GlowGeometry.MaxBlurSigma, layout.WashSigma);
        Assert.Equal((int)MathF.Ceiling(3f * GlowGeometry.MaxBlurSigma) + 1, layout.Margin);
    }

    [Fact]
    public void Segments_InTheMiddleOfAnEdge_AreOneRectangleOfTheBandThickness()
    {
        var (layout, zones, segments, count) = Build(new LedLayoutSettings(), Defaults, washLayer: true);
        var middleTop = zones.Select((zone, index) => (zone, index))
            .Single(entry => entry.zone.Edge == ScreenEdge.Top && entry.zone.Region.X <= 0.5f && entry.zone.Region.Right > 0.5f);

        var owned = segments.Take(count).Where(segment => ZoneOf(segment) == middleTop.index).ToArray();

        var segment = Assert.Single(owned);
        float margin = layout.Margin;
        Assert.Equal(0f, segment.Top);                                     // reaches into the margin
        Assert.Equal(layout.WashSpread + margin, segment.Bottom, precision: 4);
        Assert.Equal((middleTop.zone.Region.X * layout.RenderWidth) + margin, segment.Left, precision: 3);
        Assert.Equal((middleTop.zone.Region.Right * layout.RenderWidth) + margin, segment.Right, precision: 3);
    }

    [Theory]
    [InlineData(0.03f)]
    [InlineData(0.20f)]
    [InlineData(0.35f)]
    [InlineData(0.50f)]
    public void Segments_NeverOverlap_SoNoAreaIsBlendedTwice(float spread)
    {
        var settings = Defaults with { SpreadFraction = spread };
        var (_, _, segments, count) = Build(new LedLayoutSettings(), settings, washLayer: true);

        for (var a = 0; a < count; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                var overlapWidth = MathF.Min(segments[a].Right, segments[b].Right) - MathF.Max(segments[a].Left, segments[b].Left);
                var overlapHeight = MathF.Min(segments[a].Bottom, segments[b].Bottom) - MathF.Max(segments[a].Top, segments[b].Top);
                Assert.False(overlapWidth > 1e-3f && overlapHeight > 1e-3f, $"Segments {a} and {b} overlap.");
            }
        }
    }

    [Theory]
    [InlineData(3840, 2160, 0.20f)]
    [InlineData(3840, 2160, 0.50f)]
    [InlineData(1920, 1200, 0.35f)]
    [InlineData(1080, 1920, 0.35f)]  // portrait
    public void Segments_CoverTheMiteredFrame_EachPointByTheZonesOfItsNearestEdge(int width, int height, float spread)
    {
        var settings = Defaults with { SpreadFraction = spread };
        var (layout, zones, segments, count) = Build(new LedLayoutSettings(), settings, washLayer: true, width, height);
        AssertMiteredCoverage(layout, zones, segments.AsSpan(0, count), layout.WashSpread, allEdges: true);
    }

    [Fact]
    public void Segments_CoverTheInnerLayerToo()
    {
        var (layout, zones, segments, count) = Build(new LedLayoutSettings(), Defaults, washLayer: false);
        AssertMiteredCoverage(layout, zones, segments.AsSpan(0, count), layout.InnerSpread, allEdges: true);
    }

    [Fact]
    public void Segments_AtHalfTheScreen_KeepEveryEdgeInTheWash()
    {
        // With square corners the top and bottom bands would cover the whole height and the side zones would vanish.
        var (_, zones, segments, count) = Build(new LedLayoutSettings(), Defaults with { SpreadFraction = 0.5f }, washLayer: true);

        foreach (var edge in new[] { ScreenEdge.Top, ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right })
        {
            var area = segments.Take(count)
                .Where(segment => zones[ZoneOf(segment)].Edge == edge)
                .Sum(segment => (segment.Right - segment.Left) * (segment.Bottom - segment.Top));
            Assert.True(area > 1000f, $"The {edge} edge has only {area:F0} px² of wash.");
        }
    }

    [Fact]
    public void Segments_WithoutABottomEdge_LetTheSidesRunToTheBottomCorner()
    {
        var (layout, zones, segments, count) = Build(new LedLayoutSettings { BottomCount = 0 }, Defaults, washLayer: true);

        var lowestSide = segments.Take(count)
            .Where(segment => zones[ZoneOf(segment)].Edge is ScreenEdge.Left or ScreenEdge.Right)
            .Max(segment => segment.Bottom);
        Assert.Equal(layout.CanvasHeight, lowestSide);
        AssertMiteredCoverage(layout, zones, segments.AsSpan(0, count), layout.WashSpread, allEdges: false);
    }

    [Fact]
    public void Segments_NeverExceedTheDocumentedBufferSize()
    {
        foreach (var ledLayout in new[]
        {
            new LedLayoutSettings(),
            new LedLayoutSettings { TopCount = 300, RightCount = 212, BottomCount = 300, LeftCount = 212 },
            new LedLayoutSettings { TopCount = 1, RightCount = 1, BottomCount = 1, LeftCount = 1 },
        })
        {
            foreach (var spread in new[] { 0.005f, 0.2f, 0.5f })
            {
                var (_, zones, _, count) = Build(ledLayout, Defaults with { SpreadFraction = spread }, washLayer: true);
                Assert.InRange(count, zones.Length, GlowGeometry.MaxSegmentCount(zones.Length));
            }
        }
    }

    [Fact]
    public void Segments_CarryBrightnessAndAlpha()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = Enumerable.Repeat(new ColorRgb(255, 128, 0), zones.Length).ToArray();
        var segments = new GlowSegment[GlowGeometry.MaxSegmentCount(zones.Length)];
        var layout = GlowGeometry.ComputeLayout(1920, 1080, Defaults);

        GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, layout.InnerSpread, brightness: 0.5f, alpha: 0.6f, segments);

        Assert.Equal(0.5f, segments[0].Red, precision: 5);
        Assert.Equal(128f / 255f * 0.5f, segments[0].Green, precision: 5);
        Assert.Equal(0f, segments[0].Blue);
        Assert.Equal(0.6f, segments[0].Alpha);
    }

    [Fact]
    public void Segments_RequireAColorPerZone_AndARoomyBuffer()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var layout = GlowGeometry.ComputeLayout(1920, 1080, Defaults);

        Assert.Throws<ArgumentException>(() => GlowGeometry.BuildSegments(
            zones.AsSpan(), new ColorRgb[zones.Length - 1], layout, layout.WashSpread, 1f, 1f, new GlowSegment[GlowGeometry.MaxSegmentCount(zones.Length)]));
        Assert.Throws<ArgumentException>(() => GlowGeometry.BuildSegments(
            zones.AsSpan(), new ColorRgb[zones.Length], layout, layout.WashSpread, 1f, 1f, new GlowSegment[zones.Length]));
    }

    [Fact]
    public void PictureMask_CoversTheLetterboxedPicture_RoundedOutwards()
    {
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);

        // A 2.39:1 film on a 16:9 screen: bars of 12.8% above and below.
        Assert.True(GlowGeometry.TryGetPictureMask(layout, new NormalizedRect(0f, 0.1278f, 1f, 0.7444f), out var mask));

        Assert.Equal(new PictureMask(0f, MathF.Floor(0.1278f * 270f), 480f, MathF.Ceiling(0.8722f * 270f)), mask);
    }

    [Fact]
    public void PictureMask_CoversAPillarboxedPicture()
    {
        var layout = GlowGeometry.ComputeLayout(1920, 1080, Defaults);

        // 4:3 on 16:9: bars of 12.5% left and right.
        Assert.True(GlowGeometry.TryGetPictureMask(layout, new NormalizedRect(0.125f, 0f, 0.75f, 1f), out var mask));

        Assert.Equal(new PictureMask(30f, 0f, 210f, 135f), mask);
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 1f)]            // full screen: masking would remove the whole glow
    [InlineData(0f, 0.004f, 1f, 0.992f)]    // a sliver of black, below the 1% threshold
    [InlineData(0f, 0f, 0f, 0f)]            // invalid
    public void PictureMask_IsNotUsed_WithoutRealBars(float x, float y, float width, float height)
    {
        var layout = GlowGeometry.ComputeLayout(1920, 1080, Defaults);

        Assert.False(GlowGeometry.TryGetPictureMask(layout, new NormalizedRect(x, y, width, height), out _));
    }

    [Fact]
    public void PictureFrame_LightComesFromThePictureEdges_AndSpillsIntoTheBars()
    {
        var layout = GlowGeometry.ComputeLayout(1920, 1080, Defaults);
        Assert.True(GlowGeometry.TryGetPictureMask(layout, new NormalizedRect(0f, 0.1278f, 1f, 0.7444f), out var mask));
        var frame = GlowFrame.Picture(mask);
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = Enumerable.Range(0, zones.Length).Select(i => new ColorRgb((byte)i, 0, 0)).ToArray();
        var segments = new GlowSegment[GlowGeometry.MaxSegmentCount(zones.Length)];
        var outer = layout.WashSigma;

        var count = GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, frame, outer, layout.WashSpread, 1f, 1f, segments);

        float margin = layout.Margin;
        foreach (var segment in segments.AsSpan(0, count))
        {
            // Nothing reaches further than the outer extent beyond the picture.
            Assert.True(segment.Left >= frame.Left + margin - outer - 1e-3f);
            Assert.True(segment.Right <= frame.Left + frame.Width + margin + outer + 1e-3f);
            Assert.True(segment.Top >= frame.Top + margin - outer - 1e-3f);
            Assert.True(segment.Bottom <= frame.Top + frame.Height + margin + outer + 1e-3f);
        }

        // The top band starts in the bar just above the picture, not at the bezel.
        var topBand = segments.Take(count).Where(segment => zones[ZoneOf(segment)].Edge == ScreenEdge.Top).ToArray();
        Assert.Equal(frame.Top + margin - outer, topBand.Min(segment => segment.Top), precision: 3);

        // Every point of the picture within the band, away from the corner diagonals, is lit exactly once.
        var tolerance = ((outer + MathF.Min(layout.WashSpread, frame.Height / 2f)) / GlowGeometry.CornerSlices) + 1f;
        for (var y = 0.5f; y < frame.Height; y += 1f)
        {
            for (var x = 0.5f; x < frame.Width; x += 1f)
            {
                var distances = new[] { y, frame.Height - y, x, frame.Width - x }.Order().ToArray();
                if (distances[0] > layout.WashSpread - tolerance || distances[1] - distances[0] < tolerance)
                {
                    continue;
                }

                var hits = segments.Take(count).Count(segment =>
                    x + frame.Left + margin >= segment.Left && x + frame.Left + margin < segment.Right &&
                    y + frame.Top + margin >= segment.Top && y + frame.Top + margin < segment.Bottom);
                Assert.True(hits == 1, $"Picture point ({x}, {y}) is lit {hits} times.");
            }
        }
    }

    /// <summary>
    /// Why bands extend into the margin: blur the inner band in 1D and compare the glow intensity at the very
    /// edge of the screen with and without the extension.
    /// </summary>
    [Fact]
    public void MarginExtension_KeepsTheInnerGlowAtFullStrengthAtTheScreenEdge()
    {
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);

        var extended = BlurredAtScreenEdge(layout.InnerSpread, layout.InnerSigma, bandStart: -layout.Margin);
        var clipped = BlurredAtScreenEdge(layout.InnerSpread, layout.InnerSigma, bandStart: 0);

        // Inner glow: width 8.1 px, sigma 5.4 px. The extended band loses only the kernel tail beyond the width,
        // Phi(7.6 / 5.4) = 92%; a band clipped at the edge also loses the half outside the screen.
        Assert.InRange(extended, 0.9f, 1f);
        Assert.InRange(clipped, 0.4f, 0.55f);
        Assert.True(extended > 1.6f * clipped, $"Extension only raises the edge from {clipped:P1} to {extended:P1}.");
    }

    [Fact]
    public void BuildSegments_AllocatesNothing()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = new ColorRgb[zones.Length];
        var segments = new GlowSegment[GlowGeometry.MaxSegmentCount(zones.Length)];
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);
        GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, layout.WashSpread, 1f, 1f, segments);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, layout.InnerSpread, 1f, 1f, segments);
            GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, layout.WashSpread, 1f, 1f, segments);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Samples the canvas every render pixel. A point whose nearest edge (by signed distance, so the margin is
    /// included) is clearly within the band must be covered exactly once, by a zone of that edge; a point
    /// clearly beyond the band must not be covered. Points within one corner slice of a diagonal or of the band
    /// boundary are where the staircase approximates the exact shape, and are only checked for overlap.
    /// </summary>
    private static void AssertMiteredCoverage(
        GlowLayout layout,
        ImmutableArray<ZoneConfig> zones,
        ReadOnlySpan<GlowSegment> segments,
        float spread,
        bool allEdges)
    {
        float width = layout.RenderWidth;
        float height = layout.RenderHeight;
        float margin = layout.Margin;
        var hasBottom = zones.Any(zone => zone.Edge == ScreenEdge.Bottom);
        var tolerance = ((margin + MathF.Min(spread, MathF.Min(width, height) / 2f)) / GlowGeometry.CornerSlices) + 1f;
        Span<float> distances = stackalloc float[4];
        var checkedPoints = 0;

        for (var y = -margin + 0.5f; y < height + margin; y += 1f)
        {
            for (var x = -margin + 0.5f; x < width + margin; x += 1f)
            {
                distances[0] = y;                                   // top
                distances[1] = allEdges || hasBottom ? height - y : float.PositiveInfinity;
                distances[2] = x;                                   // left
                distances[3] = width - x;                           // right

                var owner = 0;
                for (var e = 1; e < 4; e++)
                {
                    if (distances[e] < distances[owner])
                    {
                        owner = e;
                    }
                }

                var runnerUp = float.PositiveInfinity;
                for (var e = 0; e < 4; e++)
                {
                    if (e != owner)
                    {
                        runnerUp = MathF.Min(runnerUp, distances[e]);
                    }
                }

                var depth = owner switch
                {
                    0 => MathF.Min(spread, hasBottom ? height / 2f : height),
                    1 => MathF.Min(spread, height / 2f),
                    _ => MathF.Min(spread, width / 2f),
                };

                var hits = 0;
                var hitEdge = ScreenEdge.Top;
                foreach (var segment in segments)
                {
                    var cx = x + margin;
                    var cy = y + margin;
                    if (cx >= segment.Left && cx < segment.Right && cy >= segment.Top && cy < segment.Bottom)
                    {
                        hits++;
                        hitEdge = zones[ZoneOf(segment)].Edge;
                    }
                }

                Assert.True(hits <= 1, $"Point ({x}, {y}) is covered {hits} times.");
                var nearDiagonal = runnerUp - distances[owner] < tolerance;
                var nearBoundary = MathF.Abs(distances[owner] - depth) < tolerance;
                if (nearDiagonal || nearBoundary)
                {
                    continue;
                }

                checkedPoints++;
                var expectedEdge = owner switch
                {
                    0 => ScreenEdge.Top,
                    1 => ScreenEdge.Bottom,
                    2 => ScreenEdge.Left,
                    _ => ScreenEdge.Right,
                };

                if (distances[owner] < depth)
                {
                    Assert.True(hits == 1, $"Point ({x}, {y}) inside the {expectedEdge} band is not covered.");
                    Assert.Equal(expectedEdge, hitEdge);
                }
                else
                {
                    Assert.True(hits == 0, $"Point ({x}, {y}) beyond the band is covered.");
                }
            }
        }

        Assert.True(checkedPoints > (width * height) / 2, "Too few points were checked strictly.");
    }

    private static float BlurredAtScreenEdge(float spread, float sigma, float bandStart)
    {
        // Discrete Gaussian centred on the first screen row (y = 0.5), band covers [bandStart, spread).
        var radius = (int)MathF.Ceiling(4f * sigma);
        double weighted = 0;
        double total = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var y = 0.5f + offset;
            var weight = Math.Exp(-(offset * offset) / (2.0 * sigma * sigma));
            total += weight;
            if (y >= bandStart && y < spread)
            {
                weighted += weight;
            }
        }

        return (float)(weighted / total);
    }

    /// <summary>Zone index encoded in the red channel by <see cref="Build"/>.</summary>
    private static int ZoneOf(GlowSegment segment) => (int)MathF.Round(segment.Red * 255f);

    private static (GlowLayout Layout, ImmutableArray<ZoneConfig> Zones, GlowSegment[] Segments, int Count) Build(
        LedLayoutSettings ledLayout,
        OverlaySettings settings,
        bool washLayer,
        int width = 3840,
        int height = 2160)
    {
        var zones = ZoneLayoutBuilder.Build(ledLayout);
        var layout = GlowGeometry.ComputeLayout(width, height, settings);
        var colors = Enumerable.Range(0, zones.Length).Select(i => new ColorRgb((byte)i, 0, 0)).ToArray();
        var segments = new GlowSegment[GlowGeometry.MaxSegmentCount(zones.Length)];
        var spread = washLayer ? layout.WashSpread : layout.InnerSpread;
        var count = GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, spread, 1f, 1f, segments);
        return (layout, zones, segments, count);
    }
}
