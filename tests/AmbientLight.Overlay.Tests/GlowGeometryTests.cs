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
    public void Layout_4K_RendersAtOneEighth_AndScalesBackToTheExactScreenSize()
    {
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);

        Assert.Equal(480, layout.RenderWidth);
        Assert.Equal(270, layout.RenderHeight);
        Assert.Equal(8f, layout.ScaleX);
        Assert.Equal(8f, layout.ScaleY);
        Assert.Equal(0.04f * 270f, layout.Spread, precision: 4);      // spread: 4% of the shorter side
        Assert.Equal(0.08f * 270f / 3f, layout.BlurSigma, precision: 4); // radius = 3 sigma
        Assert.Equal((int)MathF.Ceiling(3f * layout.BlurSigma) + 1, layout.Margin);
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
    public void Segments_OnePerZone_OnTheirOwnEdge()
    {
        var (layout, zones, segments, count) = Build(new LedLayoutSettings());

        Assert.Equal(zones.Length, count);
        float margin = layout.Margin;
        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            switch (zones[i].Edge)
            {
                case ScreenEdge.Top:
                    Assert.Equal(0f, segment.Top);                                    // reaches into the margin
                    Assert.Equal(layout.Spread + margin, segment.Bottom, precision: 4);
                    break;
                case ScreenEdge.Bottom:
                    Assert.Equal(layout.RenderHeight - layout.Spread + margin, segment.Top, precision: 4);
                    Assert.Equal(layout.CanvasHeight, segment.Bottom);
                    break;
                case ScreenEdge.Left:
                    Assert.Equal(0f, segment.Left);
                    Assert.Equal(layout.Spread + margin, segment.Right, precision: 4);
                    break;
                case ScreenEdge.Right:
                    Assert.Equal(layout.RenderWidth - layout.Spread + margin, segment.Left, precision: 4);
                    Assert.Equal(layout.CanvasWidth, segment.Right);
                    break;
            }
        }
    }

    [Fact]
    public void Segments_NeverOverlap_SoCornersAreNotBlendedTwice()
    {
        var (_, _, segments, count) = Build(new LedLayoutSettings());

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

    [Fact]
    public void Segments_CoverTheWholeBandAroundTheScreen_IncludingCorners()
    {
        var (layout, _, segments, count) = Build(new LedLayoutSettings());
        float margin = layout.Margin;
        var inset = layout.Spread / 2f;

        // Walk the middle line of the band all around the screen, corners included.
        for (var t = 0f; t <= 1f; t += 0.002f)
        {
            AssertCovered(margin + (t * layout.RenderWidth), margin + inset);
            AssertCovered(margin + (t * layout.RenderWidth), margin + layout.RenderHeight - inset);
            AssertCovered(margin + inset, margin + (t * layout.RenderHeight));
            AssertCovered(margin + layout.RenderWidth - inset, margin + (t * layout.RenderHeight));
        }

        void AssertCovered(float x, float y)
        {
            var hits = 0;
            for (var i = 0; i < count; i++)
            {
                if (x >= segments[i].Left && x < segments[i].Right && y >= segments[i].Top && y < segments[i].Bottom)
                {
                    hits++;
                }
            }

            Assert.True(hits == 1, $"Point ({x:F1}, {y:F1}) is covered by {hits} segments.");
        }
    }

    [Fact]
    public void Segments_WithoutABottomEdge_LetTheSidesRunToTheBottomCorner()
    {
        var (layout, zones, segments, count) = Build(new LedLayoutSettings { BottomCount = 0 });

        var lowestSide = segments.Take(count)
            .Where((segment, i) => zones[i].Edge is ScreenEdge.Left or ScreenEdge.Right)
            .Max(segment => segment.Bottom);
        Assert.Equal(layout.CanvasHeight, lowestSide);
    }

    [Fact]
    public void Segments_CarryBrightnessAndOpacity()
    {
        var settings = Defaults with { Brightness = 0.5f, Opacity = 0.6f };
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = Enumerable.Repeat(new ColorRgb(255, 128, 0), zones.Length).ToArray();
        var segments = new GlowSegment[zones.Length];

        GlowGeometry.BuildSegments(zones.AsSpan(), colors, GlowGeometry.ComputeLayout(1920, 1080, settings), settings, segments);

        Assert.Equal(0.5f, segments[0].Red, precision: 5);
        Assert.Equal(128f / 255f * 0.5f, segments[0].Green, precision: 5);
        Assert.Equal(0f, segments[0].Blue);
        Assert.Equal(0.6f, segments[0].Alpha);
    }

    [Fact]
    public void Segments_RequireAColorPerZone()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());

        Assert.Throws<ArgumentException>(() => GlowGeometry.BuildSegments(
            zones.AsSpan(),
            new ColorRgb[zones.Length - 1],
            GlowGeometry.ComputeLayout(1920, 1080, Defaults),
            Defaults,
            new GlowSegment[zones.Length]));
    }

    /// <summary>
    /// Why bands extend into the margin: blur a top band in 1D and compare the glow intensity at the very
    /// edge of the screen with and without the extension.
    /// </summary>
    [Fact]
    public void MarginExtension_KeepsTheGlowAtFullStrengthAtTheScreenEdge()
    {
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);

        var extended = BlurredAtScreenEdge(layout, bandStart: -layout.Margin);
        var clipped = BlurredAtScreenEdge(layout, bandStart: 0);

        // Default settings: spread 10.8 px, sigma 7.2 px. The extended band loses only the kernel tail beyond
        // the spread, Phi(10.3 / 7.2) = 92%; a band clipped at the edge also loses the half outside the screen.
        Assert.InRange(extended, 0.9f, 1f);
        Assert.InRange(clipped, 0.4f, 0.55f);
        Assert.True(extended > 1.6f * clipped, $"Extension only raises the edge from {clipped:P1} to {extended:P1}.");
    }

    [Fact]
    public void BuildSegments_AllocatesNothing()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = new ColorRgb[zones.Length];
        var segments = new GlowSegment[zones.Length];
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);
        GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, Defaults, segments);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, Defaults, segments);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static float BlurredAtScreenEdge(GlowLayout layout, float bandStart)
    {
        // Discrete Gaussian centred on the first screen row (y = 0.5), band covers [bandStart, spread).
        var sigma = layout.BlurSigma;
        var radius = (int)MathF.Ceiling(4f * sigma);
        double weighted = 0;
        double total = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var y = 0.5f + offset;
            var weight = Math.Exp(-(offset * offset) / (2.0 * sigma * sigma));
            total += weight;
            if (y >= bandStart && y < layout.Spread)
            {
                weighted += weight;
            }
        }

        return (float)(weighted / total);
    }

    private static (GlowLayout Layout, ImmutableArray<ZoneConfig> Zones, GlowSegment[] Segments, int Count) Build(LedLayoutSettings ledLayout)
    {
        var zones = ZoneLayoutBuilder.Build(ledLayout);
        var layout = GlowGeometry.ComputeLayout(3840, 2160, Defaults);
        var colors = Enumerable.Range(0, zones.Length).Select(i => new ColorRgb((byte)i, 0, 0)).ToArray();
        var segments = new GlowSegment[zones.Length];
        var count = GlowGeometry.BuildSegments(zones.AsSpan(), colors, layout, Defaults, segments);
        return (layout, zones, segments, count);
    }
}
