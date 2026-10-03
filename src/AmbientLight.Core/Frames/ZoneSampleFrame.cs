using System.Numerics;
using AmbientLight.Core.Zones;

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
    /// <summary>
    /// Lines per axis in the luminance profile; must equal <c>PROFILE_RESOLUTION</c> in ZoneReduce.hlsl.
    /// 256 lines resolve a bar to 0.4% of the screen height (4 px at 1080p).
    /// </summary>
    public const int ProfileResolution = 256;

    private readonly Vector3[] _samples;
    private readonly float[] _lineLuma = new float[ProfileResolution * 2];

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

    /// <summary>
    /// The visible-desktop rectangle the zones were sampled within: the full screen, or the content area
    /// below/between black bars when letterbox detection has cropped them away.
    /// </summary>
    public NormalizedRect ContentBounds { get; set; } = NormalizedRect.Full;

    /// <summary>True when <see cref="RowLuma"/> and <see cref="ColumnLuma"/> hold this frame's profile.</summary>
    public bool HasProfile { get; set; }

    /// <summary>
    /// Brightest linear luma (1.0 = SDR white) found along each visible row, top to bottom, over the whole
    /// screen regardless of <see cref="ContentBounds"/>. Input to letterbox detection.
    /// </summary>
    public Span<float> RowLuma => _lineLuma.AsSpan(0, ProfileResolution);

    /// <summary>Brightest linear luma along each visible column, left to right. Input to pillarbox detection.</summary>
    public Span<float> ColumnLuma => _lineLuma.AsSpan(ProfileResolution, ProfileResolution);

    /// <summary>Rows then columns, contiguous, exactly as the GPU writes them (2 x <see cref="ProfileResolution"/>).</summary>
    public Span<float> LineLuma => _lineLuma;

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
        ContentBounds = source.ContentBounds;
        HasProfile = source.HasProfile;
        if (source.HasProfile)
        {
            source._lineLuma.CopyTo(_lineLuma, 0);
        }
    }
}
