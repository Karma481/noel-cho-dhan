using System.Runtime.InteropServices;
using AmbientLight.Core.Color;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Frames;

/// <summary>
/// Output of the Processing stage, consumed by the overlay renderer and the serial writer.
/// </summary>
/// <remarks>
/// <para>
/// Two color sets are produced from the same smoothed, saturated, temperature-adjusted signal because
/// the two outputs need different final transforms:
/// </para>
/// <list type="bullet">
/// <item><see cref="LedColors"/>: for the physical strip. Strip white balance, brightness, LED gamma and
/// the power limiter applied. In strip (wire) order and packed to 3 bytes per LED, so
/// <see cref="LedBytes"/> is exactly the Adalight payload and goes to the port without copying.</item>
/// <item><see cref="DisplayColors"/>: for the on-screen glow. Standard sRGB encoding, which is what a
/// monitor expects; none of the LED-specific corrections, which would distort it.</item>
/// </list>
/// <para>
/// Instances are pre-allocated and recycled by <see cref="Threading.TripleBuffer{T}"/>; the steady state
/// allocates nothing, which keeps the GC silent on the pipeline threads.
/// </para>
/// </remarks>
public sealed class FrameData : ICopyFrom<FrameData>
{
    private readonly ColorRgb[] _ledColors;
    private readonly ColorRgb[] _displayColors;

    /// <summary>Creates a frame able to hold up to <paramref name="capacity"/> zones.</summary>
    public FrameData(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ledColors = new ColorRgb[capacity];
        _displayColors = new ColorRgb[capacity];
    }

    /// <summary>Sequence number of the capture this frame was derived from.</summary>
    public long Sequence { get; set; }

    /// <summary>Settings version whose zone layout these colors follow.</summary>
    public long LayoutVersion { get; set; }

    /// <summary>Timestamps from capture through processing.</summary>
    public FrameTiming Timing { get; set; }

    /// <summary>Width in pixels of the captured output.</summary>
    public int SourceWidth { get; set; }

    /// <summary>Height in pixels of the captured output.</summary>
    public int SourceHeight { get; set; }

    /// <summary>
    /// True while temporal smoothing is still converging towards the latest capture. The processing
    /// stage keeps producing frames on a timer in that state, even when the desktop is static.
    /// </summary>
    public bool IsTransitioning { get; set; }

    /// <summary>Picture area the colors were sampled from (smaller than the screen when black bars are cropped).</summary>
    public NormalizedRect ContentBounds { get; set; } = NormalizedRect.Full;

    /// <summary>Estimated strip current for <see cref="LedColors"/>, after power limiting, in milliamps.</summary>
    public int EstimatedCurrentMilliamps { get; set; }

    /// <summary>Scale the power limiter applied to the LED colors (1 = not limited).</summary>
    public float PowerLimitScale { get; set; } = 1f;

    /// <summary>Number of valid zones.</summary>
    public int ZoneCount { get; private set; }

    /// <summary>Maximum number of zones this frame can hold.</summary>
    public int Capacity => _ledColors.Length;

    /// <summary>LED strip colors in zone (wire) order.</summary>
    public Span<ColorRgb> LedColors => _ledColors.AsSpan(0, ZoneCount);

    /// <summary>The LED colors as raw R,G,B bytes (3 x <see cref="ZoneCount"/>): the Adalight payload.</summary>
    public ReadOnlySpan<byte> LedBytes => MemoryMarshal.AsBytes(_ledColors.AsSpan(0, ZoneCount));

    /// <summary>sRGB-encoded colors for the on-screen overlay, in zone order.</summary>
    public Span<ColorRgb> DisplayColors => _displayColors.AsSpan(0, ZoneCount);

    /// <summary>Sets the number of valid zones.</summary>
    public void SetZoneCount(int zoneCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoneCount, Capacity);
        ZoneCount = zoneCount;
    }

    /// <inheritdoc />
    public void CopyFrom(FrameData source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SetZoneCount(source.ZoneCount);
        source.LedColors.CopyTo(_ledColors);
        source.DisplayColors.CopyTo(_displayColors);
        Sequence = source.Sequence;
        LayoutVersion = source.LayoutVersion;
        Timing = source.Timing;
        SourceWidth = source.SourceWidth;
        SourceHeight = source.SourceHeight;
        IsTransitioning = source.IsTransitioning;
        ContentBounds = source.ContentBounds;
        EstimatedCurrentMilliamps = source.EstimatedCurrentMilliamps;
        PowerLimitScale = source.PowerLimitScale;
    }
}
