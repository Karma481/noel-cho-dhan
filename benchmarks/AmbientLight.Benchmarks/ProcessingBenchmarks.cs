using System.Diagnostics;
using System.Numerics;
using AmbientLight.Core.Color;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Processing.Letterbox;
using AmbientLight.Processing.Pipeline;
using BenchmarkDotNet.Attributes;

namespace AmbientLight.Benchmarks;

/// <summary>
/// Per-frame cost of the processing stage. The budget is a few microseconds per frame; the
/// allocation column must read 0 B (also asserted by ColorPipelineTests.SteadyState_AllocatesNothing).
/// </summary>
[MemoryDiagnoser]
public class ProcessingBenchmarks
{
    private static readonly long FrameTicks = Stopwatch.Frequency / 60;

    private ColorPipeline _pipeline = null!;
    private SettingsSnapshot _snapshot = null!;
    private ZoneSampleFrame _sample = null!;
    private FrameData _output = null!;
    private LetterboxDetector _detector = null!;
    private LetterboxParameters _letterboxParameters;
    private ColorRgb[] _leds = null!;
    private PowerBudget _budget;
    private long _now;

    /// <summary>LED count: a typical 27" setup and the configured maximum.</summary>
    [Params(100, 1024)]
    public int Zones { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var perSide = Zones / 4;
        _snapshot = new SettingsHolder(new AppSettings
        {
            LedLayout = new LedLayoutSettings
            {
                TopCount = perSide,
                RightCount = perSide,
                BottomCount = perSide,
                LeftCount = Zones - (3 * perSide),
            },
            Serial = new SerialSettings { MaxCurrentMilliamps = 400 },
        }).Current;

        _pipeline = new ColorPipeline(LedLayoutSettings.MaxLedCount);
        _output = new FrameData(LedLayoutSettings.MaxLedCount);
        _sample = new ZoneSampleFrame(LedLayoutSettings.MaxLedCount);
        _sample.SetZoneCount(Zones);
        _sample.LayoutVersion = _snapshot.Version;
        _sample.HasProfile = true;
        _sample.ColumnLuma.Fill(0.5f);
        _sample.RowLuma.Fill(0.5f);
        _sample.RowLuma[..33].Clear();
        _sample.RowLuma[^33..].Clear();

        var random = new Random(42);
        for (var i = 0; i < Zones; i++)
        {
            _sample.Samples[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
        }

        _detector = new LetterboxDetector();
        _letterboxParameters = LetterboxParameters.From(new LetterboxSettings(), ZoneSampleFrame.ProfileResolution);

        _leds = new ColorRgb[Zones];
        for (var i = 0; i < Zones; i++)
        {
            _leds[i] = new ColorRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        }

        _budget = new PowerBudget(400, 20, 1);
        _now = FrameTicks;

        // Every benchmark runs in its own process: give the pipeline a target so RenderOnly can run.
        _pipeline.Ingest(_sample, _snapshot, _now);
        _pipeline.Render(_now, _snapshot, _output);
    }

    /// <summary>One full capture → output step: black threshold, letterbox, smoothing, grading, encoding, power limit.</summary>
    [Benchmark(Baseline = true)]
    public bool IngestAndRender()
    {
        _now += FrameTicks;
        _sample.Timing = new FrameTiming(_now, _now, 0);
        _pipeline.Ingest(_sample, _snapshot, _now);
        return _pipeline.Render(_now, _snapshot, _output);
    }

    /// <summary>A smoothing tick without a new capture (static screen while a fade completes).</summary>
    [Benchmark]
    public bool RenderOnly()
    {
        _now += FrameTicks;
        return _pipeline.Render(_now, _snapshot, _output);
    }

    /// <summary>Letterbox detection over the 2 x 256-line profile.</summary>
    [Benchmark]
    public bool LetterboxUpdate()
    {
        _now += FrameTicks;
        return _detector.Update(_sample.RowLuma, _sample.ColumnLuma, _now, _letterboxParameters);
    }

    /// <summary>Power limiting of an over-budget frame (restores the input first so every call limits).</summary>
    [Benchmark]
    public int PowerLimit()
    {
        Span<ColorRgb> frame = stackalloc ColorRgb[_leds.Length];
        _leds.CopyTo(frame);
        return PowerLimiter.Apply(frame, _budget).DeliveredMilliamps;
    }
}
