using System.Diagnostics;
using System.Numerics;
using AmbientLight.Core.Color;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Processing.Pipeline;

namespace AmbientLight.Processing.Tests;

public sealed class ColorPipelineTests
{
    private const int Zones = 100;

    private static readonly long Frame = Stopwatch.Frequency / 60;

    /// <summary>Neutral grading: what goes in comes out, so individual steps can be tested in isolation.</summary>
    private static AppSettings Neutral(Func<AppSettings, AppSettings>? customize = null)
    {
        var settings = new AppSettings
        {
            Processing = new ProcessingSettings
            {
                SmoothingTimeMs = 0,
                Saturation = 1f,
                Brightness = 1f,
                ColorTemperatureK = 6500,
                WhiteBalance = RgbGain.Identity,
                LedGamma = 2.2f,
                BlackThreshold = 0,
            },
            Serial = new SerialSettings { MaxCurrentMilliamps = 0 },
            LedLayout = new LedLayoutSettings { TopCount = 30, RightCount = 20, BottomCount = 30, LeftCount = 20 },
        };
        return customize is null ? settings : customize(settings);
    }

    [Fact]
    public void DisplayColors_AreSrgbEncoded_LedColors_TrackLinearLight()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral());
        Fill(sample, new Vector3(0.2159f)); // linear 21.6% = sRGB 128/255

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Assert.Equal(new ColorRgb(128, 128, 128), output.DisplayColors[0]);
        Assert.InRange(output.LedColors[0].R, 52, 58); // ~21% PWM duty
    }

    [Fact]
    public void BlackThreshold_ForcesNearBlackToOff()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { BlackThreshold = 10 },
        }));
        Fill(sample, new Vector3(0.002f)); // sRGB ≈ 6/255, below 10
        sample.Samples[1] = new Vector3(0.01f); // sRGB ≈ 25/255, above

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Assert.Equal(ColorRgb.Black, output.LedColors[0]);
        Assert.Equal(ColorRgb.Black, output.DisplayColors[0]);
        Assert.NotEqual(ColorRgb.Black, output.DisplayColors[1]);
    }

    [Fact]
    public void WhiteBalanceAndBrightness_AffectLedsOnly()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { Brightness = 0.5f, WhiteBalance = new RgbGain(1f, 1f, 0.5f) },
        }));
        Fill(sample, Vector3.One);

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Assert.Equal(ColorRgb.White, output.DisplayColors[0]);
        var led = output.LedColors[0];
        Assert.Equal(led.R, led.G);
        Assert.True(led.B < led.R);
        Assert.InRange(led.R, 120, 135); // 50% light
    }

    [Fact]
    public void ColorTemperatureAndSaturation_AffectBothOutputs()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { ColorTemperatureK = 3000 },
        }));
        Fill(sample, Vector3.One);

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Assert.True(output.DisplayColors[0].B < output.DisplayColors[0].R);
        Assert.True(output.LedColors[0].B < output.LedColors[0].R);
    }

    [Fact]
    public void Smoothing_FadesTowardsANewScene_AndReportsTransition()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { SmoothingTimeMs = 100 },
        }));
        Fill(sample, Vector3.Zero);
        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Fill(sample, Vector3.One);
        pipeline.Ingest(sample, snapshot, 2 * Frame);
        var transitioning = pipeline.Render(2 * Frame, snapshot, output);

        Assert.True(transitioning);
        Assert.True(output.IsTransitioning);
        Assert.InRange(output.DisplayColors[0].R, 1, 254);

        var now = 2 * Frame;
        for (var i = 0; i < 120 && pipeline.Render(now += Frame, snapshot, output); i++)
        {
        }

        Assert.False(output.IsTransitioning);
        Assert.Equal(ColorRgb.White, output.DisplayColors[0]);
    }

    [Fact]
    public void LongStaticPeriod_DoesNotSkipTheFade()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { SmoothingTimeMs = 200 },
        }));
        Fill(sample, Vector3.Zero);
        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        // Ten minutes later the scene cuts to white.
        var later = Frame + (600 * Stopwatch.Frequency);
        Fill(sample, Vector3.One);
        pipeline.Ingest(sample, snapshot, later);
        pipeline.Render(later, snapshot, output);

        Assert.True(output.IsTransitioning);
        Assert.True(output.DisplayColors[0].R < 255);
    }

    [Fact]
    public void LayoutChange_ResetsSmoothing()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Processing = s.Processing with { SmoothingTimeMs = 500 },
        }));
        Fill(sample, Vector3.Zero);
        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Fill(sample, Vector3.One);
        sample.LayoutVersion++;
        pipeline.Ingest(sample, snapshot, 2 * Frame);
        pipeline.Render(2 * Frame, snapshot, output);

        Assert.Equal(ColorRgb.White, output.DisplayColors[0]);
        Assert.False(output.IsTransitioning);
    }

    [Fact]
    public void SettingsChange_AppliesOnRender_WithoutANewCapture()
    {
        var holder = new SettingsHolder(Neutral());
        var (pipeline, _, sample, output) = Create(Neutral());
        Fill(sample, Vector3.One);
        pipeline.Ingest(sample, holder.Current, Frame);
        pipeline.Render(Frame, holder.Current, output);
        var before = output.LedColors[0];

        Assert.True(holder.TryPublish(holder.Current.Settings with
        {
            Processing = holder.Current.Settings.Processing with { Brightness = 0.25f },
        }, out _));
        pipeline.Render(2 * Frame, holder.Current, output);

        Assert.True(output.LedColors[0].R < before.R);
    }

    [Fact]
    public void PowerLimiter_KeepsFullWhiteWithinTheUsbBudget()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Serial = s.Serial with { MaxCurrentMilliamps = 400, MilliampsPerChannel = 20, IdleMilliampsPerLed = 1 },
        }));
        Fill(sample, Vector3.One); // 100 white LEDs would draw 6.1 A

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(Frame, snapshot, output);

        Assert.True(output.EstimatedCurrentMilliamps <= 400, $"{output.EstimatedCurrentMilliamps} mA");
        Assert.True(output.PowerLimitScale < 0.1f);
        Assert.Equal(ColorRgb.White, output.DisplayColors[0]); // the overlay is not power-limited
    }

    [Fact]
    public void Letterbox_IsDetectedFromTheProfile_AndCanBeDisabled()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral(s => s with
        {
            Letterbox = new LetterboxSettings { StableTimeMs = 0 },
        }));
        Fill(sample, new Vector3(0.5f));
        sample.HasProfile = true;
        sample.RowLuma.Fill(0.5f);
        sample.ColumnLuma.Fill(0.5f);
        sample.RowLuma[..33].Clear();
        sample.RowLuma[^33..].Clear();

        pipeline.Ingest(sample, snapshot, Frame);
        sample.Timing = new FrameTiming(0, 2 * Frame, 0);
        Assert.True(pipeline.Ingest(sample, snapshot, 2 * Frame));
        Assert.Equal(33f / 256f, pipeline.DetectedContentBounds.Y, precision: 6);

        var disabled = new SettingsHolder(Neutral(s => s with { Letterbox = new LetterboxSettings { Enabled = false } })).Current;
        Assert.True(pipeline.Ingest(sample, disabled, 3 * Frame));
        Assert.Equal(NormalizedRect.Full, pipeline.DetectedContentBounds);
    }

    [Fact]
    public void Output_CarriesFrameMetadata()
    {
        var (pipeline, snapshot, sample, output) = Create(Neutral());
        Fill(sample, Vector3.One);
        sample.Sequence = 42;
        sample.SourceWidth = 3840;
        sample.SourceHeight = 2160;
        sample.ContentBounds = new NormalizedRect(0f, 0.1f, 1f, 0.8f);
        sample.Timing = new FrameTiming(100, 200, 0);

        pipeline.Ingest(sample, snapshot, Frame);
        pipeline.Render(300, snapshot, output);

        Assert.Equal(Zones, output.ZoneCount);
        Assert.Equal(42, output.Sequence);
        Assert.Equal(3840, output.SourceWidth);
        Assert.Equal(sample.ContentBounds, output.ContentBounds);
        Assert.Equal(new FrameTiming(100, 200, 300), output.Timing);
    }

    [Fact]
    public void SteadyState_AllocatesNothing()
    {
        var (pipeline, snapshot, sample, output) = Create(new SettingsHolder(new AppSettings
        {
            LedLayout = new LedLayoutSettings { TopCount = 30, RightCount = 20, BottomCount = 30, LeftCount = 20 },
        }).Current.Settings);
        var random = new Random(7);
        sample.HasProfile = true;
        sample.ColumnLuma.Fill(0.5f);

        void RunFrame(long now)
        {
            for (var i = 0; i < sample.ZoneCount; i++)
            {
                sample.Samples[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
            }

            // Alternate bars so the letterbox detector also changes state.
            sample.RowLuma.Fill(0.5f);
            var bar = (int)(now / Frame % 2) * 33;
            sample.RowLuma[..bar].Clear();
            sample.RowLuma[^(bar + 1)..].Clear();
            sample.Timing = new FrameTiming(now, now, 0);
            pipeline.Ingest(sample, snapshot, now);
            pipeline.Render(now, snapshot, output);
        }

        var now = Frame;
        for (var i = 0; i < 200; i++)
        {
            RunFrame(now += Frame); // warm up: JIT, tiering, static initializers
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            RunFrame(now += Frame);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static (ColorPipeline Pipeline, SettingsSnapshot Snapshot, ZoneSampleFrame Sample, FrameData Output) Create(AppSettings settings)
    {
        var snapshot = new SettingsHolder(settings).Current;
        var sample = new ZoneSampleFrame(LedLayoutSettings.MaxLedCount);
        sample.SetZoneCount(snapshot.Zones.Length);
        sample.LayoutVersion = snapshot.Version;
        return (new ColorPipeline(LedLayoutSettings.MaxLedCount), snapshot, sample, new FrameData(LedLayoutSettings.MaxLedCount));
    }

    private static void Fill(ZoneSampleFrame sample, Vector3 color) => sample.Samples.Fill(color);
}
