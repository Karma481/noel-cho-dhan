using System.Runtime.InteropServices;
using AmbientLight.Core.Color;

namespace AmbientLight.Core.Frames;

/// <summary>
/// Output of the Processing stage: final, display-ready colors for every zone, consumed by the
/// overlay renderer and the serial writer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Colors"/> is in strip (wire) order and <see cref="ColorRgb"/> is packed to 3 bytes, so
/// <see cref="ColorBytes"/> is exactly the Adalight payload and is written to the port without copying.
/// </para>
/// <para>
/// Instances are pre-allocated and recycled by <see cref="Threading.TripleBuffer{T}"/>; the steady state
/// allocates nothing, which keeps the GC silent on the pipeline threads.
/// </para>
/// </remarks>
public sealed class FrameData : ICopyFrom<FrameData>
{
    private readonly ColorRgb[] _colors;

    /// <summary>Creates a frame able to hold up to <paramref name="capacity"/> zones.</summary>
    public FrameData(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _colors = new ColorRgb[capacity];
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

    /// <summary>Number of valid zones.</summary>
    public int ZoneCount { get; private set; }

    /// <summary>Maximum number of zones this frame can hold.</summary>
    public int Capacity => _colors.Length;

    /// <summary>The valid colors in zone (wire) order.</summary>
    public Span<ColorRgb> Colors => _colors.AsSpan(0, ZoneCount);

    /// <summary>The valid colors as raw R,G,B bytes (3 x <see cref="ZoneCount"/>).</summary>
    public ReadOnlySpan<byte> ColorBytes => MemoryMarshal.AsBytes(_colors.AsSpan(0, ZoneCount));

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
        source.Colors.CopyTo(_colors);
        Sequence = source.Sequence;
        LayoutVersion = source.LayoutVersion;
        Timing = source.Timing;
        SourceWidth = source.SourceWidth;
        SourceHeight = source.SourceHeight;
        IsTransitioning = source.IsTransitioning;
    }
}
