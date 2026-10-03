using AmbientLight.Core.Settings;
using AmbientLight.Host.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmbientLight.Host.Tests;

public sealed class PipelinePlanTests
{
    private static readonly AppSettings OverlayOnly = new();

    private static readonly AppSettings OverlayAndLeds = new()
    {
        Serial = new SerialSettings { Enabled = true, PortName = "COM3" },
    };

    private static readonly AppSettings LedsOnly = OverlayAndLeds with
    {
        Overlay = new OverlaySettings { Enabled = false },
    };

    private static readonly AppSettings NothingEnabled = new()
    {
        Overlay = new OverlaySettings { Enabled = false },
    };

    [Fact]
    public void Defaults_RunTheOverlayWithoutTheSerialStage()
    {
        // The laptop default: virtual overlay only, LED strip off, so no serial thread at all.
        var plan = PipelinePlan.Create(OverlayOnly, paused: false, exclusiveFullscreen: false);

        Assert.Equal(new PipelinePlan(true, true, true, false, false, PipelineMode.Running), plan);
        Assert.False(new AppSettings().Serial.Enabled);
    }

    [Fact]
    public void Pause_StopsEverything()
    {
        var plan = PipelinePlan.Create(OverlayAndLeds, paused: true, exclusiveFullscreen: false);

        Assert.Equal(new PipelinePlan(false, false, false, false, false, PipelineMode.Paused), plan);
    }

    [Fact]
    public void ExclusiveFullscreen_WithOverlayOnly_StopsCaptureToo()
    {
        var plan = PipelinePlan.Create(OverlayOnly, paused: false, exclusiveFullscreen: true);

        Assert.Equal(new PipelinePlan(false, false, false, false, true, PipelineMode.SuspendedForFullscreen), plan);
    }

    [Fact]
    public void ExclusiveFullscreen_WithLeds_KeepsCaptureForTheStrip()
    {
        var plan = PipelinePlan.Create(OverlayAndLeds, paused: false, exclusiveFullscreen: true);

        Assert.Equal(new PipelinePlan(true, true, false, true, true, PipelineMode.Running), plan);
    }

    [Fact]
    public void ExclusiveFullscreen_IsIgnoredWhenTheOptionIsOff()
    {
        var settings = OverlayOnly with { Performance = new PerformanceSettings { PauseOverlayInExclusiveFullscreen = false } };

        var plan = PipelinePlan.Create(settings, paused: false, exclusiveFullscreen: true);

        Assert.Equal(new PipelinePlan(true, true, true, false, false, PipelineMode.Running), plan);
    }

    [Fact]
    public void LedsOnly_RunsWithoutTheOverlay()
    {
        var plan = PipelinePlan.Create(LedsOnly, paused: false, exclusiveFullscreen: false);

        Assert.Equal(new PipelinePlan(true, true, false, true, false, PipelineMode.Running), plan);
    }

    [Fact]
    public void NoOutput_IsIdleWithEveryStageStopped()
    {
        var plan = PipelinePlan.Create(NothingEnabled, paused: false, exclusiveFullscreen: false);

        Assert.Equal(new PipelinePlan(false, false, false, false, false, PipelineMode.Idle), plan);
    }
}

public sealed class PipelineOrchestratorTests
{
    private readonly List<string> _log = [];
    private readonly FakeStage _capture;
    private readonly FakeStage _processing;
    private readonly FakeStage _overlay;
    private readonly FakeStage _serial;

    public PipelineOrchestratorTests()
    {
        _capture = new FakeStage("Capture", _log);
        _processing = new FakeStage("Processing", _log);
        _overlay = new FakeStage("Overlay", _log);
        _serial = new FakeStage("Serial", _log);
    }

    [Fact]
    public void Start_WithDefaults_StartsConsumersFirstAndNeverTouchesSerial()
    {
        var (orchestrator, _) = Create(new AppSettings());

        orchestrator.Start();

        Assert.Equal(["start Overlay", "start Processing", "start Capture"], _log);
        Assert.Equal(0, _serial.Starts);
        Assert.Equal(PipelineMode.Running, orchestrator.State.Plan.Mode);
        Assert.Null(orchestrator.State.LastError);
    }

    [Fact]
    public void Start_WithLeds_StartsSerialBeforeTheProducers()
    {
        var (orchestrator, _) = Create(new AppSettings { Serial = new SerialSettings { Enabled = true, PortName = "COM5" } });

        orchestrator.Start();

        Assert.Equal(["start Serial", "start Overlay", "start Processing", "start Capture"], _log);
    }

    [Fact]
    public void Pause_StopsProducersFirst_AndResumeRestartsConsumersFirst()
    {
        var (orchestrator, _) = Create(new AppSettings());
        orchestrator.Start();
        _log.Clear();

        Assert.True(orchestrator.TogglePause());
        Assert.Equal(["stop Capture", "stop Processing", "stop Overlay"], _log);
        Assert.True(orchestrator.State.IsPaused);
        Assert.Equal(PipelineMode.Paused, orchestrator.State.Plan.Mode);

        _log.Clear();
        Assert.False(orchestrator.TogglePause());
        Assert.Equal(["start Overlay", "start Processing", "start Capture"], _log);
        Assert.False(orchestrator.State.IsPaused);
    }

    [Fact]
    public void ExclusiveFullscreen_WithOverlayOnly_ReleasesTheWholePipeline()
    {
        var (orchestrator, _) = Create(new AppSettings());
        orchestrator.Start();
        _log.Clear();

        orchestrator.SetExclusiveFullscreen(true);

        Assert.Equal(["stop Capture", "stop Processing", "stop Overlay"], _log);
        Assert.Equal(PipelineMode.SuspendedForFullscreen, orchestrator.State.Plan.Mode);

        _log.Clear();
        orchestrator.SetExclusiveFullscreen(false);

        Assert.Equal(["start Overlay", "start Processing", "start Capture"], _log);
    }

    [Fact]
    public void ExclusiveFullscreen_WithLeds_StopsOnlyTheOverlay()
    {
        var (orchestrator, _) = Create(new AppSettings { Serial = new SerialSettings { Enabled = true, PortName = "COM5" } });
        orchestrator.Start();
        _log.Clear();

        orchestrator.SetExclusiveFullscreen(true);

        Assert.Equal(["stop Overlay"], _log);
        Assert.True(orchestrator.State.Plan.OverlaySuspended);
        Assert.True(_capture.IsRunning);
        Assert.True(_serial.IsRunning);
    }

    [Fact]
    public void SettingsChanges_AreReconciledAutomatically()
    {
        var (orchestrator, holder) = Create(new AppSettings());
        orchestrator.Start();
        _log.Clear();

        // Turning the overlay off leaves no consumer: everything stops.
        Publish(holder, settings => settings with { Overlay = settings.Overlay with { Enabled = false } });
        Assert.Equal(["stop Capture", "stop Processing", "stop Overlay"], _log);
        Assert.Equal(PipelineMode.Idle, orchestrator.State.Plan.Mode);

        // Turning the LED strip on brings back the sources it needs, consumer first.
        _log.Clear();
        Publish(holder, settings => settings with { Serial = settings.Serial with { Enabled = true, PortName = "COM7" } });
        Assert.Equal(["start Serial", "start Processing", "start Capture"], _log);
        Assert.False(_overlay.IsRunning);
    }

    [Fact]
    public void IrrelevantSettingsChanges_TouchNoStageAndRaiseNoEvent()
    {
        var (orchestrator, holder) = Create(new AppSettings());
        orchestrator.Start();
        _log.Clear();
        var events = 0;
        orchestrator.StateChanged += (_, _) => events++;

        for (var i = 1; i <= 10; i++)
        {
            var brightness = i / 10f;
            Publish(holder, settings => settings with { Overlay = settings.Overlay with { Brightness = brightness } });
        }

        Assert.Empty(_log);
        Assert.Equal(0, events);
    }

    [Fact]
    public void StateChanged_IsRaisedOncePerChange()
    {
        var (orchestrator, _) = Create(new AppSettings());
        var states = new List<PipelineState>();
        orchestrator.StateChanged += (_, state) => states.Add(state);

        orchestrator.Start();
        orchestrator.SetPaused(true);
        orchestrator.SetPaused(true);
        orchestrator.SetPaused(false);

        Assert.Equal([PipelineMode.Running, PipelineMode.Paused, PipelineMode.Running], states.Select(state => state.Plan.Mode));
    }

    [Fact]
    public void FailedStart_IsReported_AndRetriedAtTheNextReconciliation()
    {
        var (orchestrator, _) = Create(new AppSettings());
        _capture.FailStartWith = new PlatformNotSupportedException("Windows 10 2004 required.");

        orchestrator.Start();

        Assert.False(_capture.IsRunning);
        Assert.True(_overlay.IsRunning);
        Assert.Contains("Capture could not start", orchestrator.State.LastError, StringComparison.Ordinal);

        // Still failing: the next event retries and reports it again.
        orchestrator.SetExclusiveFullscreen(false);
        Assert.Equal(0, _capture.Starts);
        Assert.Contains("Capture could not start", orchestrator.State.LastError, StringComparison.Ordinal);

        // Fixed: the next event starts it and clears the error.
        _capture.FailStartWith = null;
        orchestrator.SetExclusiveFullscreen(false);
        Assert.True(_capture.IsRunning);
        Assert.Null(orchestrator.State.LastError);
    }

    [Fact]
    public void FailedStop_KeepsTheStageRunning_AndRetriesTheStop()
    {
        var (orchestrator, _) = Create(new AppSettings());
        orchestrator.Start();
        _overlay.FailStopWith = new TimeoutException("The overlay thread did not stop within 5 s.");

        orchestrator.SetPaused(true);

        Assert.True(_overlay.IsRunning);
        Assert.False(_capture.IsRunning);
        Assert.Contains("Overlay did not stop", orchestrator.State.LastError, StringComparison.Ordinal);

        _overlay.FailStopWith = null;
        orchestrator.SetExclusiveFullscreen(true);

        Assert.False(_overlay.IsRunning);
        Assert.Null(orchestrator.State.LastError);
    }

    [Fact]
    public void Stop_StopsEveryStage_AndIgnoresLaterSettings()
    {
        var (orchestrator, holder) = Create(new AppSettings { Serial = new SerialSettings { Enabled = true, PortName = "COM5" } });
        orchestrator.Start();
        _log.Clear();

        orchestrator.Stop();

        Assert.Equal(["stop Capture", "stop Processing", "stop Overlay", "stop Serial"], _log);
        Assert.Equal(PipelineMode.Stopped, orchestrator.State.Plan.Mode);

        _log.Clear();
        Publish(holder, settings => settings with { Overlay = settings.Overlay with { Opacity = 0.5f } });
        Assert.Empty(_log);
    }

    [Fact]
    public void NothingRuns_BeforeStart()
    {
        var (orchestrator, _) = Create(new AppSettings());

        orchestrator.SetPaused(false);
        orchestrator.SetExclusiveFullscreen(false);

        Assert.Empty(_log);
        Assert.Equal(PipelinePlan.AllStopped, orchestrator.State.Plan);
    }

    private static void Publish(SettingsHolder holder, Func<AppSettings, AppSettings> change)
    {
        Assert.True(holder.TryPublish(change(holder.Current.Settings), out var issues), string.Join("; ", issues));
    }

    private (PipelineOrchestrator Orchestrator, SettingsHolder Holder) Create(AppSettings settings)
    {
        var holder = new SettingsHolder(settings);
        var stages = new PipelineStages(_capture, _processing, _overlay, _serial);
        return (new PipelineOrchestrator(holder, stages, NullLogger<PipelineOrchestrator>.Instance), holder);
    }

    private sealed class FakeStage(string name, List<string> log) : IPipelineStage
    {
        public string Name => name;

        public bool IsRunning { get; private set; }

        public int Starts { get; private set; }

        public Exception? FailStartWith { get; set; }

        public Exception? FailStopWith { get; set; }

        public void StartStage()
        {
            if (FailStartWith is { } failure)
            {
                throw failure;
            }

            IsRunning = true;
            Starts++;
            log.Add("start " + name);
        }

        public void StopStage()
        {
            if (FailStopWith is { } failure)
            {
                throw failure;
            }

            IsRunning = false;
            log.Add("stop " + name);
        }
    }
}

public sealed class DelegatingStageTests
{
    [Fact]
    public void StartAndStop_AreIdempotent()
    {
        var starts = 0;
        var stops = 0;
        var stage = new DelegatingStage("Test", () => starts++, () => stops++);

        stage.StartStage();
        stage.StartStage();
        stage.StopStage();
        stage.StopStage();

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void FailedStart_LeavesTheStageStopped()
    {
        var stage = new DelegatingStage("Test", () => throw new InvalidOperationException("no"), () => { });

        Assert.Throws<InvalidOperationException>(stage.StartStage);
        Assert.False(stage.IsRunning);
    }

    [Fact]
    public void FailedStop_LeavesTheStageRunning()
    {
        var stage = new DelegatingStage("Test", () => { }, () => throw new TimeoutException());
        stage.StartStage();

        Assert.Throws<TimeoutException>(stage.StopStage);
        Assert.True(stage.IsRunning);
    }
}
