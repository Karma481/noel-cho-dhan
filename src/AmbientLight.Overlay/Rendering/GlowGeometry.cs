using AmbientLight.Core.Color;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;

namespace AmbientLight.Overlay.Rendering;

/// <summary>
/// Sizes of the two glow layers for one monitor and one set of overlay settings.
/// All lengths except the screen size are in render pixels (screen pixels / divisor).
/// </summary>
/// <param name="ScreenWidth">Monitor width in physical pixels.</param>
/// <param name="ScreenHeight">Monitor height in physical pixels.</param>
/// <param name="RenderWidth">Swapchain width.</param>
/// <param name="RenderHeight">Swapchain height.</param>
/// <param name="ScaleX">DirectComposition scale that stretches the swapchain over the monitor horizontally.</param>
/// <param name="ScaleY">Same, vertically.</param>
/// <param name="InnerSpread">Thickness of the inner glow's solid band along each edge.</param>
/// <param name="InnerSigma">Standard deviation of the inner glow's Gaussian blur.</param>
/// <param name="WashSpread">Thickness of the ambient wash's solid band along each edge.</param>
/// <param name="WashSigma">Standard deviation of the ambient wash's Gaussian blur.</param>
/// <param name="Margin">
/// Off-screen border around the drawing canvases, wide enough for the larger blur. Bands extend into it so
/// that pixels at the screen edge are blurred against more of the same color instead of transparency, which
/// would darken the very edge.
/// </param>
public readonly record struct GlowLayout(
    int ScreenWidth,
    int ScreenHeight,
    int RenderWidth,
    int RenderHeight,
    float ScaleX,
    float ScaleY,
    float InnerSpread,
    float InnerSigma,
    float WashSpread,
    float WashSigma,
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

/// <summary>An area of the swapchain (render pixels) that is cleared after the glow is drawn.</summary>
/// <param name="Left">Left edge, inclusive.</param>
/// <param name="Top">Top edge, inclusive.</param>
/// <param name="Right">Right edge, exclusive.</param>
/// <param name="Bottom">Bottom edge, exclusive.</param>
public readonly record struct PictureMask(float Left, float Top, float Right, float Bottom);

/// <summary>
/// The rectangle (render pixels) whose edges the glow emanates from: the whole screen, or the picture of a
/// letterboxed video, so the light then spills from the picture into the black bars like a video player's
/// ambient mode.
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct GlowFrame(float Left, float Top, float Width, float Height)
{
    /// <summary>The whole swapchain.</summary>
    public static GlowFrame Screen(in GlowLayout layout) => new(0f, 0f, layout.RenderWidth, layout.RenderHeight);

    /// <summary>The picture area of a letterboxed or pillarboxed video.</summary>
    public static GlowFrame Picture(PictureMask mask) => new(mask.Left, mask.Top, mask.Right - mask.Left, mask.Bottom - mask.Top);
}

/// <summary>Turns zones and their display colors into glow bands. Pure and allocation-free.</summary>
/// <remarks>
/// <para>
/// Each zone owns the part of the frame around the screen that is closer to its edge than to any other edge
/// (a picture frame with mitered corners), up to the layer's band thickness. The bands of one layer therefore
/// never overlap, so no area is alpha-blended twice, and every edge keeps its share of the screen even when
/// the ambient wash is so wide that the bands of opposite edges meet in the middle (with square corners, the
/// top and bottom bands would swallow the left and right ones).
/// </para>
/// <para>
/// A mitered corner is a diagonal, approximated by a staircase of <see cref="CornerSlices"/> rectangles per edge
/// and corner. Both edges meeting at a corner step on the same grid of distances from the corner; in each grid
/// cell the horizontal edge stops at the cell's near boundary and the vertical edge at its far boundary, so
/// the two staircases interlock without gap or overlap. At 1/8 resolution and under a blur the steps are
/// invisible. Bands that touch a screen corner continue into the canvas margin (the diagonal included), so the
/// blur sees color beyond the edge and the glow stays at full strength right up to the bezel.
/// </para>
/// </remarks>
public static class GlowGeometry
{
    /// <summary>Upper bound of the Direct2D Gaussian blur standard deviation.</summary>
    public const float MaxBlurSigma = 250f;

    /// <summary>Rectangles approximating each mitered corner diagonal, per edge.</summary>
    public const int CornerSlices = 12;

    /// <summary>Blur radius of the inner glow as a multiple of its band width.</summary>
    public const float InnerBlurToWidth = 2f;

    private const float EdgeEpsilon = 1e-4f;

    /// <summary>
    /// Size of a segment buffer that always fits one layer: one band per zone, plus the slices of the eight
    /// corner ends (four edges, two ends each), each of which can also split one zone in two.
    /// </summary>
    public static int MaxSegmentCount(int zoneCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneCount);
        return zoneCount + (8 * (CornerSlices + 1));
    }

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

        var innerSpread = settings.InnerGlowFraction * shorter;
        var innerSigma = MathF.Min(InnerBlurToWidth * innerSpread / 3f, MaxBlurSigma);
        var washSpread = settings.SpreadFraction * shorter;
        var washSigma = MathF.Min(settings.BlurRadiusFraction * shorter / 3f, MaxBlurSigma);
        var margin = (int)MathF.Ceiling(3f * MathF.Max(innerSigma, washSigma)) + 1;

        return new GlowLayout(
            screenWidth,
            screenHeight,
            renderWidth,
            renderHeight,
            screenWidth / (float)renderWidth,
            screenHeight / (float)renderHeight,
            innerSpread,
            innerSigma,
            washSpread,
            washSigma,
            margin);
    }

    /// <summary>Smallest black bar, as a fraction of the screen side, for which <see cref="TryGetPictureMask"/> masks the picture.</summary>
    public const float MinimumBarFraction = 0.01f;

    /// <summary>
    /// The swapchain area covered by the picture of a letterboxed or pillarboxed video, kept clear of glow when
    /// <see cref="OverlaySettings.KeepPictureClear"/> is on. Returns <see langword="false"/> when the picture fills
    /// the screen (no bar of at least <see cref="MinimumBarFraction"/>), where masking would remove the glow.
    /// </summary>
    /// <remarks>
    /// The rectangle is rounded outwards to whole render pixels. DirectComposition upscales the swapchain
    /// bilinearly, so the glow fades out over one render pixel; rounding outwards puts that fade in the bars and
    /// keeps the picture itself untouched.
    /// </remarks>
    public static bool TryGetPictureMask(in GlowLayout layout, NormalizedRect content, out PictureMask mask)
    {
        var hasBars =
            content.X >= MinimumBarFraction ||
            content.Y >= MinimumBarFraction ||
            1f - content.Right >= MinimumBarFraction ||
            1f - content.Bottom >= MinimumBarFraction;
        if (!content.IsValid || !hasBars || content.Width <= 0f || content.Height <= 0f)
        {
            mask = default;
            return false;
        }

        float width = layout.RenderWidth;
        float height = layout.RenderHeight;
        mask = new PictureMask(
            MathF.Max(0f, MathF.Floor(content.X * width)),
            MathF.Max(0f, MathF.Floor(content.Y * height)),
            MathF.Min(width, MathF.Ceiling(content.Right * width)),
            MathF.Min(height, MathF.Ceiling(content.Bottom * height)));
        return true;
    }

    /// <summary>
    /// Writes the bands of one layer along the screen edges into <paramref name="destination"/> (canvas
    /// coordinates) and returns how many were written. Bands extend through the whole canvas margin.
    /// </summary>
    /// <param name="zones">Zones in any order.</param>
    /// <param name="colors">Display color of each zone (sRGB).</param>
    /// <param name="layout">Monitor layout.</param>
    /// <param name="spread">Band thickness of this layer (<see cref="GlowLayout.InnerSpread"/> or <see cref="GlowLayout.WashSpread"/>).</param>
    /// <param name="brightness">Multiplier of the colors, 0..1.</param>
    /// <param name="alpha">Opacity of every band, 0..1.</param>
    /// <param name="destination">At least <see cref="MaxSegmentCount"/>(zone count) entries.</param>
    public static int BuildSegments(
        ReadOnlySpan<ZoneConfig> zones,
        ReadOnlySpan<ColorRgb> colors,
        in GlowLayout layout,
        float spread,
        float brightness,
        float alpha,
        Span<GlowSegment> destination) =>
        BuildSegments(zones, colors, layout, GlowFrame.Screen(layout), layout.Margin, spread, brightness, alpha, destination);

    /// <summary>
    /// Writes the bands of one layer along the edges of <paramref name="frame"/> into <paramref name="destination"/>
    /// (canvas coordinates) and returns how many were written.
    /// </summary>
    /// <param name="zones">Zones in any order.</param>
    /// <param name="colors">Display color of each zone (sRGB).</param>
    /// <param name="layout">Monitor layout.</param>
    /// <param name="frame">Rectangle whose edges the light comes from.</param>
    /// <param name="outerExtent">
    /// How far bands continue outwards beyond the frame edge (and beyond its corners): the canvas margin for the
    /// screen, the layer's blur sigma for a picture, which keeps the light near full strength right next to the
    /// picture before it fades towards the bezel.
    /// </param>
    /// <param name="spread">Band thickness of this layer, inwards from the frame edge.</param>
    /// <param name="brightness">Multiplier of the colors, 0..1.</param>
    /// <param name="alpha">Opacity of every band, 0..1.</param>
    /// <param name="destination">At least <see cref="MaxSegmentCount"/>(zone count) entries.</param>
    public static int BuildSegments(
        ReadOnlySpan<ZoneConfig> zones,
        ReadOnlySpan<ColorRgb> colors,
        in GlowLayout layout,
        in GlowFrame frame,
        float outerExtent,
        float spread,
        float brightness,
        float alpha,
        Span<GlowSegment> destination)
    {
        if (colors.Length < zones.Length)
        {
            throw new ArgumentException("A color is required for every zone.", nameof(colors));
        }

        if (destination.Length < MaxSegmentCount(zones.Length))
        {
            throw new ArgumentException($"The destination must hold {MaxSegmentCount(zones.Length)} segments.", nameof(destination));
        }

        var edges = new EdgePresence(zones);
        var scale = brightness / byte.MaxValue;

        // Shared by all four corners so the staircases of neighbouring edges use the same grid.
        var cornerExtent = MathF.Max(0f, MathF.Min(spread, MathF.Min(frame.Width, frame.Height) / 2f));
        var outer = MathF.Max(0f, MathF.Min(outerExtent, layout.Margin));
        var count = 0;

        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            if (zone.Edge is not (ScreenEdge.Top or ScreenEdge.Bottom or ScreenEdge.Left or ScreenEdge.Right))
            {
                continue;
            }

            var color = colors[i];
            var band = new Band(zone, layout, frame, outer, spread, cornerExtent, edges, color.R * scale, color.G * scale, color.B * scale, alpha);
            count = band.Emit(destination, count);
        }

        return count;
    }

    /// <summary>Which screen edges have at least one zone.</summary>
    private readonly ref struct EdgePresence
    {
        public EdgePresence(ReadOnlySpan<ZoneConfig> zones)
        {
            foreach (var zone in zones)
            {
                Top |= zone.Edge == ScreenEdge.Top;
                Bottom |= zone.Edge == ScreenEdge.Bottom;
                Left |= zone.Edge == ScreenEdge.Left;
                Right |= zone.Edge == ScreenEdge.Right;
            }
        }

        public bool Top { get; }

        public bool Bottom { get; }

        public bool Left { get; }

        public bool Right { get; }
    }

    /// <summary>
    /// One zone's band in edge-local coordinates of the frame: <c>u</c> runs along the edge (left to right, or top
    /// to bottom), <c>v</c> is the depth inwards from the edge (negative values lie outside the frame).
    /// </summary>
    private readonly ref struct Band
    {
        private readonly ScreenEdge _edge;
        private readonly bool _horizontal;
        private readonly float _cornerExtent;
        private readonly float _width;
        private readonly float _height;
        private readonly float _outer;
        private readonly float _offsetX;
        private readonly float _offsetY;
        private readonly float _length;
        private readonly float _depth;
        private readonly bool _startCorner;
        private readonly bool _endCorner;
        private readonly float _u0;
        private readonly float _u1;
        private readonly float _red;
        private readonly float _green;
        private readonly float _blue;
        private readonly float _alpha;

        public Band(
            ZoneConfig zone,
            in GlowLayout layout,
            in GlowFrame frame,
            float outer,
            float spread,
            float cornerExtent,
            in EdgePresence edges,
            float red,
            float green,
            float blue,
            float alpha)
        {
            _edge = zone.Edge;
            _horizontal = _edge is ScreenEdge.Top or ScreenEdge.Bottom;
            _cornerExtent = cornerExtent;
            _width = frame.Width;
            _height = frame.Height;
            _outer = outer;
            _offsetX = frame.Left + layout.Margin;
            _offsetY = frame.Top + layout.Margin;
            _red = red;
            _green = green;
            _blue = blue;
            _alpha = alpha;

            _length = _horizontal ? _width : _height;
            var across = _horizontal ? _height : _width;

            // A corner is mitered only where the perpendicular edge has its own band; otherwise this band fills it.
            _startCorner = _horizontal ? edges.Left : edges.Top;
            _endCorner = _horizontal ? edges.Right : edges.Bottom;
            var opposite = _edge switch
            {
                ScreenEdge.Top => edges.Bottom,
                ScreenEdge.Bottom => edges.Top,
                ScreenEdge.Left => edges.Right,
                _ => edges.Left,
            };

            // Closer to this edge than to the opposite one: never deeper than half the screen when both have bands.
            _depth = MathF.Max(0f, MathF.Min(spread, opposite ? across / 2f : across));

            var region = zone.Region;
            var start = _horizontal ? region.X : region.Y;
            var end = _horizontal ? region.Right : region.Bottom;
            _u0 = start <= EdgeEpsilon ? -_outer : start * _length;
            _u1 = end >= 1f - EdgeEpsilon ? _length + _outer : end * _length;
        }

        /// <summary>Appends this band's rectangles to <paramref name="destination"/> from <paramref name="count"/> on.</summary>
        public int Emit(Span<GlowSegment> destination, int count)
        {
            if (_depth <= 0f || _u1 <= _u0)
            {
                return count;
            }

            var flatStart = float.NegativeInfinity;
            var flatEnd = float.PositiveInfinity;
            var step = (_outer + _cornerExtent) / CornerSlices;

            if (_startCorner)
            {
                // Cells at distances [low, high] from the corner along this edge (u = distance).
                for (var k = 0; k < CornerSlices; k++)
                {
                    var low = -_outer + (k * step);
                    var high = low + step;
                    count = EmitPiece(destination, count, low, high, _horizontal ? low : high);
                }

                flatStart = _cornerExtent;
            }

            if (_endCorner)
            {
                // Same cells mirrored: the distance from the end corner is length - u.
                for (var k = 0; k < CornerSlices; k++)
                {
                    var high = _length + _outer - (k * step);
                    var low = high - step;
                    count = EmitPiece(destination, count, low, high, _horizontal ? _length - high : _length - low);
                }

                flatEnd = _length - _cornerExtent;
            }

            if (flatEnd > flatStart)
            {
                count = EmitPiece(destination, count, flatStart, flatEnd, _depth);
            }

            return count;
        }

        /// <summary>Emits the part of this zone within [<paramref name="cellLow"/>, <paramref name="cellHigh"/>) along the edge, <paramref name="reach"/> deep.</summary>
        private int EmitPiece(Span<GlowSegment> destination, int count, float cellLow, float cellHigh, float reach)
        {
            var low = MathF.Max(cellLow, _u0);
            var high = MathF.Min(cellHigh, _u1);
            reach = MathF.Min(reach, _depth);
            if (high <= low || reach <= -_outer)
            {
                return count;
            }

            float left, top, right, bottom;
            switch (_edge)
            {
                case ScreenEdge.Top:
                    (left, right, top, bottom) = (low, high, -_outer, reach);
                    break;
                case ScreenEdge.Bottom:
                    (left, right, top, bottom) = (low, high, _height - reach, _height + _outer);
                    break;
                case ScreenEdge.Left:
                    (left, right, top, bottom) = (-_outer, reach, low, high);
                    break;
                default:
                    (left, right, top, bottom) = (_width - reach, _width + _outer, low, high);
                    break;
            }

            var segment = new GlowSegment(
                left + _offsetX,
                top + _offsetY,
                right + _offsetX,
                bottom + _offsetY,
                _red,
                _green,
                _blue,
                _alpha);

            if (segment.IsEmpty)
            {
                return count;
            }

            destination[count] = segment;
            return count + 1;
        }
    }
}
