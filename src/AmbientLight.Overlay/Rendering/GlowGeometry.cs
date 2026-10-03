using AmbientLight.Core.Color;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;

namespace AmbientLight.Overlay.Rendering;

/// <summary>
/// Sizes of the glow rendering for one monitor and one set of overlay settings.
/// All lengths except the screen size are in render pixels (screen pixels / divisor).
/// </summary>
/// <param name="ScreenWidth">Monitor width in physical pixels.</param>
/// <param name="ScreenHeight">Monitor height in physical pixels.</param>
/// <param name="RenderWidth">Swapchain width.</param>
/// <param name="RenderHeight">Swapchain height.</param>
/// <param name="ScaleX">DirectComposition scale that stretches the swapchain over the monitor horizontally.</param>
/// <param name="ScaleY">Same, vertically.</param>
/// <param name="Spread">Thickness of the solid color band along each edge.</param>
/// <param name="BlurSigma">Standard deviation of the Gaussian blur.</param>
/// <param name="Margin">
/// Off-screen border around the drawing canvas. Bands extend into it so that pixels at the screen edge are
/// blurred against more of the same color instead of transparency, which would darken the very edge.
/// </param>
public readonly record struct GlowLayout(
    int ScreenWidth,
    int ScreenHeight,
    int RenderWidth,
    int RenderHeight,
    float ScaleX,
    float ScaleY,
    float Spread,
    float BlurSigma,
    int Margin)
{
    /// <summary>Canvas width including the margin on both sides.</summary>
    public int CanvasWidth => RenderWidth + (2 * Margin);

    /// <summary>Canvas height including the margin on both sides.</summary>
    public int CanvasHeight => RenderHeight + (2 * Margin);
}

/// <summary>One solid band of the glow, in canvas coordinates, with a straight-alpha color.</summary>
public readonly record struct GlowSegment(float Left, float Top, float Right, float Bottom, float Red, float Green, float Blue, float Alpha)
{
    /// <summary>True when the band has no area (fully trimmed away).</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;
}

/// <summary>Turns zones and their display colors into glow bands. Pure and allocation-free.</summary>
/// <remarks>
/// <para>
/// Each zone owns the part of the screen edge it samples, drawn as a band <see cref="GlowLayout.Spread"/>
/// thick, then blurred by the renderer. Top and bottom bands own the corners; left and right bands are
/// trimmed to stop where the top/bottom bands begin, so no two bands overlap and no area gets
/// alpha-blended twice (which would make the corners more opaque than the edges).
/// </para>
/// <para>
/// Bands that touch a screen corner are extended into the canvas margin, so the blur sees color
/// beyond the edge and the glow stays at full strength right up to the bezel.
/// </para>
/// </remarks>
public static class GlowGeometry
{
    private const float EdgeEpsilon = 1e-4f;

    /// <summary>Computes the layout for a monitor of <paramref name="screenWidth"/> x <paramref name="screenHeight"/>.</summary>
    public static GlowLayout ComputeLayout(int screenWidth, int screenHeight, OverlaySettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);
        ArgumentNullException.ThrowIfNull(settings);

        var divisor = Math.Max(1, settings.ResolutionDivisor);
        var renderWidth = Math.Max(1, (screenWidth + divisor - 1) / divisor);
        var renderHeight = Math.Max(1, (screenHeight + divisor - 1) / divisor);
        var shorter = Math.Min(screenWidth, screenHeight) / (float)divisor;

        var spread = settings.SpreadFraction * shorter;
        var sigma = settings.BlurRadiusFraction * shorter / 3f;
        var margin = (int)MathF.Ceiling(3f * sigma) + 1;

        return new GlowLayout(
            screenWidth,
            screenHeight,
            renderWidth,
            renderHeight,
            screenWidth / (float)renderWidth,
            screenHeight / (float)renderHeight,
            spread,
            sigma,
            margin);
    }

    /// <summary>
    /// Writes one segment per zone into <paramref name="destination"/> (canvas coordinates) and returns how
    /// many were written; zones whose band is trimmed to nothing are skipped.
    /// </summary>
    public static int BuildSegments(
        ReadOnlySpan<ZoneConfig> zones,
        ReadOnlySpan<ColorRgb> colors,
        in GlowLayout layout,
        OverlaySettings settings,
        Span<GlowSegment> destination)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (colors.Length < zones.Length || destination.Length < zones.Length)
        {
            throw new ArgumentException("Colors and destination must hold at least one entry per zone.");
        }

        var hasTop = false;
        var hasBottom = false;
        foreach (var zone in zones)
        {
            hasTop |= zone.Edge == ScreenEdge.Top;
            hasBottom |= zone.Edge == ScreenEdge.Bottom;
        }

        float width = layout.RenderWidth;
        float height = layout.RenderHeight;
        float margin = layout.Margin;
        var spread = layout.Spread;
        var brightness = settings.Brightness / byte.MaxValue;
        var alpha = settings.Opacity;
        var count = 0;

        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            var region = zone.Region;
            float left, top, right, bottom;

            switch (zone.Edge)
            {
                case ScreenEdge.Top:
                case ScreenEdge.Bottom:
                    left = region.X <= EdgeEpsilon ? -margin : region.X * width;
                    right = region.Right >= 1f - EdgeEpsilon ? width + margin : region.Right * width;
                    (top, bottom) = zone.Edge == ScreenEdge.Top
                        ? (-margin, spread)
                        : (height - spread, height + margin);
                    break;

                case ScreenEdge.Left:
                case ScreenEdge.Right:
                    top = region.Y <= EdgeEpsilon && !hasTop ? -margin : region.Y * height;
                    bottom = region.Bottom >= 1f - EdgeEpsilon && !hasBottom ? height + margin : region.Bottom * height;
                    if (hasTop)
                    {
                        top = MathF.Max(top, spread);
                    }

                    if (hasBottom)
                    {
                        bottom = MathF.Min(bottom, height - spread);
                    }

                    (left, right) = zone.Edge == ScreenEdge.Left
                        ? (-margin, spread)
                        : (width - spread, width + margin);
                    break;

                default:
                    continue;
            }

            var color = colors[i];
            var segment = new GlowSegment(
                left + margin,
                top + margin,
                right + margin,
                bottom + margin,
                color.R * brightness,
                color.G * brightness,
                color.B * brightness,
                alpha);

            if (!segment.IsEmpty)
            {
                destination[count++] = segment;
            }
        }

        return count;
    }
}
