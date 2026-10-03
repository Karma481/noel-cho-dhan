using System.Numerics;

namespace AmbientLight.Core.Frames;

/// <summary>
/// Output of the Capture stage: one averaged color per zone, read back from the GPU reduction.
/// </summary>
/// <remarks>
/// <para>
/// Samples are <b>linear-light</b> RGB floats (sRGB decoded on the GPU; HDR scRGB values may exceed 1),
/// so averaging, smoothing and white balance in the Processing stage are physically correct and do not
/// band on dark gradients the way 8-bit math would.
/// </para>
/// <para>
/// Instances are pre-allocated and recycled by <see cref="Threading.TripleBuffer{T}"/>; ownership
/// belongs to whichever side currently holds the slot, so the type needs no internal synchronization.
/// </para>
/// </remarks>
public sealed class ZoneSampleFrame : ICopyFrom<ZoneSampleFrame>
{
    private readonly Vector3[] _samples;

    /// <summary>Creates a frame able to hold up to <paramref name="capacity"/> zones.</summary>
    public ZoneSampleFrame(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _samples = new Vector3[capacity];
    }

    /// <summary>Capture sequence number, strictly increasing per captured frame.</summary>
    public long Sequence { get; set; }

    /// <summary>Settings version whose zone layout produced these samples.</summary>
    public long LayoutVersion { get; set; }

    /// <summary>Capture timestamps (<see cref="FrameTiming.Processed"/> is 0 at this stage).</summary>
    public FrameTiming Timing { get; set; }

    /// <summary>Width in pixels of the captured output.</summary>
    public int SourceWidth { get; set; }

    /// <summary>Height in pixels of the captured output.</summary>
    public int SourceHeight { get; set; }

    /// <summary>True when the desktop was captured as HDR (FP16 scRGB).</summary>
    public bool IsHdr { get; set; }

    /// <summary>Number of valid zones.</summary>
    public int ZoneCount { get; private set; }

    /// <summary>Maximum number of zones this frame can hold.</summary>
    public int Capacity => _samples.Length;

    /// <summary>The valid samples, in zone (wire) order.</summary>
    public Span<Vector3> Samples => _samples.AsSpan(0, ZoneCount);

    /// <summary>Sets the number of valid zones.</summary>
    public void SetZoneCount(int zoneCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoneCount, Capacity);
        ZoneCount = zoneCount;
    }

    /// <inheritdoc />
    public void CopyFrom(ZoneSampleFrame source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SetZoneCount(source.ZoneCount);
        source.Samples.CopyTo(_samples);
        Sequence = source.Sequence;
        LayoutVersion = source.LayoutVersion;
        Timing = source.Timing;
        SourceWidth = source.SourceWidth;
        SourceHeight = source.SourceHeight;
        IsHdr = source.IsHdr;
    }
}
