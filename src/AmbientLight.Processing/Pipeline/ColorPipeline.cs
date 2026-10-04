using System.Diagnostics;
using System.Numerics;
using AmbientLight.Core.Color;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Processing.Color;
using AmbientLight.Processing.Letterbox;

namespace AmbientLight.Processing.Pipeline;

/// <summary>
/// The CPU color pipeline: turns captured linear zone samples into LED and overlay colors.
/// </summary>
/// <remarks>
/// <para>Per zone, in linear light until the final encoding:</para>
/// <code>
/// sample ─► black threshold ─► temporal smoothing ─► color temperature ─┬─► overlay grade ─► sRGB ─► DisplayColors
///                                                                        │   (saturation, contrast curve, luminance gain)
///                                                                        │
///                                                                        └─► LED saturation ─► fit to 0..1 ─► white balance × brightness
///                                                                            ─► sRGB ─► LED gamma ─► power limiter ─► LedColors
/// </code>
/// <para>
/// The overlay and the strip are graded separately: a monitor and an LED strip render the same color very
/// differently, and the overlay's look (vivid neon glow) is a matter of taste that should not change the
/// strip. Smoothing runs before the grading steps, which are static per-color transforms; that way a
/// change of the grade in the settings takes effect immediately instead of fading in. The black threshold
/// runs before smoothing so dark scenes fade out instead of snapping off.
/// </para>
/// <para>
/// All buffers are sized for <see cref="Capacity"/> zones at construction; <see cref="Ingest"/> and
/// <see cref="Render"/> allocate nothing. Not thread-safe: owned by the processing thread.
/// </para>
/// </remarks>
public sealed class ColorPipeline
{
    /// <summary>
    /// Upper bound on one smoothing step. After a long static period the next frame would otherwise see a
    /// huge elapsed time and jump straight to the new color, defeating smoothing exactly at a scene change.
    /// </summary>
    internal const float MaxStepSeconds = 0.1f;

    private readonly Vector3[] _target;
    private readonly TemporalSmoother _smoother;
    private readonly LetterboxDetector _letterbox = new();

    private ColorPipelineParameters _parameters;
    private SettingsSnapshot? _parametersSource;
    private int _zoneCount;
    private long _layoutVersion = -1;
    private long _lastRenderTicks;
    private bool _hasTarget;

    private long _sequence;
    private FrameTiming _timing;
    private int _sourceWidth;
    private int _sourceHeight;
    private NormalizedRect _sampledBounds = NormalizedRect.Full;

    /// <summary>Creates a pipeline for up to <paramref name="capacity"/> zones.</summary>
    public ColorPipeline(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _target = new Vector3[capacity];
        _smoother = new TemporalSmoother(capacity);
    }

    /// <summary>Maximum number of zones.</summary>
    public int Capacity { get; }

    /// <summary>True once at least one capture has been ingested.</summary>
    public bool HasTarget => _hasTarget;

    /// <summary>Content rectangle found by letterbox detection (the full screen when disabled or no bars).</summary>
    public NormalizedRect DetectedContentBounds => _letterbox.ContentBounds;

    /// <summary>Parameters currently in effect (diagnostics and tests).</summary>
    public ColorPipelineParameters Parameters => _parameters;

    /// <summary>
    /// Takes a new capture as the smoothing target and runs letterbox detection on its profile.
    /// Returns <see langword="true"/> when <see cref="DetectedContentBounds"/> changed.
    /// </summary>
    public bool Ingest(ZoneSampleFrame sample, SettingsSnapshot settings, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(settings);
        if (sample.ZoneCount > Capacity)
        {
            throw new ArgumentException($"{sample.ZoneCount} zones exceed the pipeline capacity of {Capacity}.", nameof(sample));
        }

        UpdateParameters(settings);

        var zoneCount = sample.ZoneCount;
        var samples = sample.Samples;
        var threshold = _parameters.BlackThresholdLinear;
        for (var i = 0; i < zoneCount; i++)
        {
            var color = Vector3.Max(samples[i], Vector3.Zero);
            _target[i] = ColorMath.Luma(color) < threshold ? Vector3.Zero : color;
        }

        // A different zone layout makes the old state meaningless: start from the new colors directly.
        if (!_hasTarget || zoneCount != _zoneCount || sample.LayoutVersion != _layoutVersion)
        {
            _smoother.Reset(_target.AsSpan(0, zoneCount));
            _zoneCount = zoneCount;
            _layoutVersion = sample.LayoutVersion;
        }

        _sequence = sample.Sequence;
        _timing = sample.Timing;
        _sourceWidth = sample.SourceWidth;
        _sourceHeight = sample.SourceHeight;
        _sampledBounds = sample.ContentBounds;
        _hasTarget = true;

        if (!_parameters.LetterboxEnabled)
        {
            return _letterbox.Reset();
        }

        if (!sample.HasProfile)
        {
            return false;
        }

        var timestamp = sample.Timing.Captured != 0 ? sample.Timing.Captured : nowTicks;
        return _letterbox.Update(sample.RowLuma, sample.ColumnLuma, timestamp, _parameters.Letterbox);
    }

    /// <summary>
    /// Advances smoothing to <paramref name="nowTicks"/> and writes the LED and overlay colors into
    /// <paramref name="output"/> using the current <paramref name="settings"/>, so a settings change shows
    /// up even while the screen is static. Returns <see langword="true"/> while smoothing has not converged.
    /// </summary>
    public bool Render(long nowTicks, SettingsSnapshot settings, FrameData output)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(output);
        if (!_hasTarget)
        {
            throw new InvalidOperationException("Ingest a capture before rendering.");
        }

        UpdateParameters(settings);

        var elapsed = _lastRenderTicks == 0
            ? 0f
            : MathF.Min((float)Stopwatch.GetElapsedTime(_lastRenderTicks, nowTicks).TotalSeconds, MaxStepSeconds);
        _lastRenderTicks = nowTicks;

        var parameters = _parameters;
        var transitioning = _smoother.Advance(_target.AsSpan(0, _zoneCount), elapsed, parameters.SmoothingSeconds);

        output.SetZoneCount(_zoneCount);
        var state = _smoother.State;
        var display = output.DisplayColors;
        var leds = output.LedColors;

        for (var i = 0; i < state.Length; i++)
        {
            var tinted = state[i] * parameters.TemperatureGains;

            var shown = ColorMath.ApplyToneCurve(
                ColorMath.FitToUnitRange(ColorMath.AdjustSaturation(tinted, parameters.DisplaySaturation)),
                parameters.DisplayContrast,
                parameters.DisplayGain);
            display[i] = new ColorRgb(
                ColorMath.LinearToSrgbByte(shown.X),
                ColorMath.LinearToSrgbByte(shown.Y),
                ColorMath.LinearToSrgbByte(shown.Z));

            var graded = ColorMath.FitToUnitRange(ColorMath.AdjustSaturation(tinted, parameters.Saturation));
            var led = Vector3.Min(graded * parameters.LedGains, Vector3.One);
            leds[i] = new ColorRgb(
                ColorMath.LinearToLedByte(led.X, parameters.LedGamma),
                ColorMath.LinearToLedByte(led.Y, parameters.LedGamma),
                ColorMath.LinearToLedByte(led.Z, parameters.LedGamma));
        }

        var power = PowerLimiter.Apply(leds, parameters.Power);

        output.Sequence = _sequence;
        output.LayoutVersion = _layoutVersion;
        output.Timing = _timing with { Processed = nowTicks };
        output.SourceWidth = _sourceWidth;
        output.SourceHeight = _sourceHeight;
        output.IsTransitioning = transitioning;
        output.ContentBounds = _sampledBounds;
        output.EstimatedCurrentMilliamps = power.DeliveredMilliamps;
        output.PowerLimitScale = power.Scale;
        return transitioning;
    }

    private void UpdateParameters(SettingsSnapshot settings)
    {
        // Snapshots are immutable and replaced on every publish, so reference identity is an exact and
        // cheap "settings changed" test (versions are only unique within one SettingsHolder).
        if (ReferenceEquals(settings, _parametersSource))
        {
            return;
        }

        _parameters = ColorPipelineParameters.From(settings.Settings);
        _parametersSource = settings;
    }
}
