using AmbientLight.Core.Settings;

namespace AmbientLight.Host.Pipeline;

/// <summary>What the pipeline is doing as a whole, for the tray tooltip and the settings window.</summary>
public enum PipelineMode
{
    /// <summary>At least one output (overlay or LED strip) is fed by capture and processing.</summary>
    Running = 0,

    /// <summary>Paused by the user (tray menu or hotkey): every stage is stopped and every thread has exited.</summary>
    Paused = 1,

    /// <summary>
    /// The overlay is the only output and it is suspended while a game runs in exclusive fullscreen, so capture
    /// and processing are stopped as well.
    /// </summary>
    SuspendedForFullscreen = 2,

    /// <summary>The overlay and the LED strip are both turned off in the settings; nothing runs.</summary>
    Idle = 3,

    /// <summary>The orchestrator has not started yet or has shut down (app exiting).</summary>
    Stopped = 4,
}

/// <summary>Which stages should run, derived from the settings and the app state by <see cref="Create"/>.</summary>
/// <param name="Capture">Desktop Duplication + GPU zone reduction (T1).</param>
/// <param name="Processing">Color pipeline (T2).</param>
/// <param name="Overlay">Glow overlay window (T3).</param>
/// <param name="Serial">LED strip output (T4).</param>
/// <param name="OverlaySuspended">The overlay is enabled but held back by exclusive fullscreen.</param>
/// <param name="Mode">Summary of the plan.</param>
public readonly record struct PipelinePlan(
    bool Capture,
    bool Processing,
    bool Overlay,
    bool Serial,
    bool OverlaySuspended,
    PipelineMode Mode)
{
    /// <summary>Every stage stopped because the orchestrator is not running (before start, after stop).</summary>
    public static PipelinePlan AllStopped { get; } = new(false, false, false, false, false, PipelineMode.Stopped);

    /// <summary>Every stage stopped because the user paused the effect.</summary>
    public static PipelinePlan Paused { get; } = new(false, false, false, false, false, PipelineMode.Paused);

    /// <summary>
    /// Derives the plan. The rules keep the laptop at minimum cost: a stage runs only when something consumes
    /// its output, so with the LED strip disabled (the default) the serial thread is never created, and with
    /// no consumer at all capture and processing stop too, releasing the GPU duplication.
    /// </summary>
    /// <param name="settings">Current settings.</param>
    /// <param name="paused">The user paused the effect.</param>
    /// <param name="exclusiveFullscreen">A Direct3D application is running in exclusive fullscreen.</param>
    public static PipelinePlan Create(AppSettings settings, bool paused, bool exclusiveFullscreen)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (paused)
        {
            return Paused;
        }

        var overlayWanted = settings.Overlay.Enabled;
        var overlaySuspended = overlayWanted && exclusiveFullscreen && settings.Performance.PauseOverlayInExclusiveFullscreen;
        var overlay = overlayWanted && !overlaySuspended;
        var serial = settings.Serial.Enabled;
        var sources = overlay || serial;

        var mode = sources
            ? PipelineMode.Running
            : overlaySuspended ? PipelineMode.SuspendedForFullscreen : PipelineMode.Idle;

        return new PipelinePlan(sources, sources, overlay, serial, overlaySuspended, mode);
    }
}
