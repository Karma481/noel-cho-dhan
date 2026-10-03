using System.Diagnostics;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Processing.Color;

namespace AmbientLight.Processing.Letterbox;

/// <summary>Detection thresholds derived once from <see cref="LetterboxSettings"/>.</summary>
/// <param name="BlackLevelLinear">Linear luma at or below which a line's brightest point counts as black.</param>
/// <param name="StableTicks">Stopwatch ticks a larger bar must persist before the crop grows to it.</param>
/// <param name="MaxBarLines">Largest bar per side, in profile lines, that is accepted as a bar.</param>
public readonly record struct LetterboxParameters(float BlackLevelLinear, long StableTicks, int MaxBarLines)
{
    /// <summary>Converts user settings into detector units for a profile of <paramref name="resolution"/> lines.</summary>
    public static LetterboxParameters From(LetterboxSettings settings, int resolution)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new LetterboxParameters(
            ColorMath.SrgbToLinear(settings.BlackLevel / 255f),
            settings.StableTimeMs * Stopwatch.Frequency / 1000,
            (int)(settings.MaxBarFraction * resolution));
    }
}

/// <summary>
/// Finds letterbox (top/bottom) and pillarbox (left/right) black bars from the per-line luminance
/// profiles produced on the GPU, and reports the content rectangle between them.
/// </summary>
/// <remarks>
/// <para>
/// Bars in films are symmetric (the picture is centered), so each axis is measured as the smaller of its
/// two black runs. This single rule rejects most false positives: a night sky makes only the top look
/// black, subtitles or a channel logo make only one bar look non-black.
/// </para>
/// <para>
/// Changes are asymmetric on purpose:
/// </para>
/// <list type="bullet">
/// <item><b>Crop grows</b> (bars appear) only after the new measurement has been stable for
/// <see cref="LetterboxSettings.StableTimeMs"/>. Cropping too early would cut real picture during a fade.</item>
/// <item><b>Crop shrinks</b> (picture now reaches into a bar on <i>both</i> sides) immediately. Sampling
/// inside the picture is always safe, while sampling black bars would make the LEDs go dark.</item>
/// </list>
/// <para>A fully black frame carries no information and leaves the result unchanged.</para>
/// </remarks>
public sealed class LetterboxDetector
{
    private readonly BarAxisTracker _vertical = new();
    private readonly BarAxisTracker _horizontal = new();
    private int _resolution;

    /// <summary>The picture area between confirmed bars, in visible-desktop coordinates.</summary>
    public NormalizedRect ContentBounds { get; private set; } = NormalizedRect.Full;

    /// <summary>Confirmed bar thickness top and bottom, in profile lines.</summary>
    public int VerticalBarLines => _vertical.ConfirmedBar;

    /// <summary>Confirmed bar thickness left and right, in profile lines.</summary>
    public int HorizontalBarLines => _horizontal.ConfirmedBar;

    /// <summary>
    /// Feeds one frame's row and column profiles (brightest linear luma per line) captured at
    /// <paramref name="timestampTicks"/>. Returns <see langword="true"/> when <see cref="ContentBounds"/> changed.
    /// </summary>
    public bool Update(ReadOnlySpan<float> rowLuma, ReadOnlySpan<float> columnLuma, long timestampTicks, in LetterboxParameters parameters)
    {
        if (rowLuma.IsEmpty || rowLuma.Length != columnLuma.Length)
        {
            throw new ArgumentException("Row and column profiles must be non-empty and of equal length.", nameof(columnLuma));
        }

        _resolution = rowLuma.Length;
        var black = parameters.BlackLevelLinear;

        var top = CountLeadingBlack(rowLuma, black);
        if (top == rowLuma.Length)
        {
            return false;
        }

        var bottom = CountTrailingBlack(rowLuma, black);
        var left = CountLeadingBlack(columnLuma, black);
        var right = CountTrailingBlack(columnLuma, black);

        var changed = _vertical.Update(top, bottom, timestampTicks, parameters);
        changed |= _horizontal.Update(left, right, timestampTicks, parameters);
        if (changed)
        {
            ContentBounds = ComputeBounds();
        }

        return changed;
    }

    /// <summary>Forgets all bars. Returns <see langword="true"/> if the bounds were not already the full screen.</summary>
    public bool Reset()
    {
        _vertical.Reset();
        _horizontal.Reset();
        var changed = ContentBounds != NormalizedRect.Full;
        ContentBounds = NormalizedRect.Full;
        return changed;
    }

    internal static int CountLeadingBlack(ReadOnlySpan<float> profile, float blackLevel)
    {
        var count = 0;
        while (count < profile.Length && profile[count] <= blackLevel)
        {
            count++;
        }

        return count;
    }

    internal static int CountTrailingBlack(ReadOnlySpan<float> profile, float blackLevel)
    {
        var count = 0;
        while (count < profile.Length && profile[profile.Length - 1 - count] <= blackLevel)
        {
            count++;
        }

        return count;
    }

    private NormalizedRect ComputeBounds()
    {
        float resolution = _resolution;
        var horizontal = _horizontal.ConfirmedBar / resolution;
        var vertical = _vertical.ConfirmedBar / resolution;
        return new NormalizedRect(horizontal, vertical, 1f - (2f * horizontal), 1f - (2f * vertical));
    }
}

/// <summary>Hysteresis for the pair of bars on one axis.</summary>
internal sealed class BarAxisTracker
{
    /// <summary>
    /// Measurements within this many lines of the confirmed bar are treated as the same bar. Absorbs the
    /// one-line jitter of a bar edge falling between two profile lines.
    /// </summary>
    internal const int ToleranceLines = 2;

    private int _candidate = -1;
    private long _candidateSince;

    /// <summary>Confirmed bar thickness per side, in profile lines.</summary>
    public int ConfirmedBar { get; private set; }

    public void Reset()
    {
        ConfirmedBar = 0;
        _candidate = -1;
    }

    /// <summary>Returns <see langword="true"/> when <see cref="ConfirmedBar"/> changed.</summary>
    public bool Update(int leading, int trailing, long timestampTicks, in LetterboxParameters parameters)
    {
        // Picture reaches into both bars: the aspect ratio changed. Shrink at once.
        if (leading < ConfirmedBar - ToleranceLines && trailing < ConfirmedBar - ToleranceLines)
        {
            _candidate = -1;
            return SetConfirmed(Math.Min(leading, trailing));
        }

        var measured = Math.Min(leading, trailing);

        // Same bar as before, picture in only one bar (subtitles, logo), or too large to be a bar (dark scene).
        if (measured <= ConfirmedBar + ToleranceLines || measured > parameters.MaxBarLines)
        {
            _candidate = -1;
            return false;
        }

        // Larger bars: confirm only after they have stayed put long enough.
        if (_candidate < 0 || Math.Abs(measured - _candidate) > ToleranceLines)
        {
            _candidate = measured;
            _candidateSince = timestampTicks;
            return false;
        }

        if (timestampTicks - _candidateSince < parameters.StableTicks)
        {
            return false;
        }

        var confirmed = Math.Min(_candidate, measured);
        _candidate = -1;
        return SetConfirmed(confirmed);
    }

    private bool SetConfirmed(int bar)
    {
        if (bar == ConfirmedBar)
        {
            return false;
        }

        ConfirmedBar = bar;
        return true;
    }
}
