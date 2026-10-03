using AmbientLight.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Host.Pipeline;

/// <summary>Snapshot of the orchestrator for the UI.</summary>
/// <param name="Plan">The stages that should run.</param>
/// <param name="IsPaused">The user paused the effect.</param>
/// <param name="IsExclusiveFullscreen">An exclusive-fullscreen Direct3D application is in front.</param>
/// <param name="LastError">Why a stage failed to start or stop in the latest reconciliation, if one did.</param>
public sealed record PipelineState(PipelinePlan Plan, bool IsPaused, bool IsExclusiveFullscreen, string? LastError);

/// <summary>
/// Decides which pipeline stages run and starts or stops them in a safe order whenever the settings, the
/// pause state or the fullscreen state change.
/// </summary>
/// <remarks>
/// <para>
/// Every change goes through one reconciliation: compute the <see cref="PipelinePlan"/>, stop the stages that
/// should not run (producers first), then start the missing ones (consumers first). Reconciling is idempotent,
/// so a slider dragged in the settings window (one publish per step) costs only a plan comparison until a
/// stage actually has to change.
/// </para>
/// <para>
/// A stage that fails to start is retried on the next reconciliation; the reason is reported in
/// <see cref="PipelineState.LastError"/>. Members are thread-safe: the UI thread, the hotkey handler and the
/// fullscreen monitor's timer may call them concurrently.
/// </para>
/// </remarks>
public sealed class PipelineOrchestrator : IDisposable
{
    private readonly SettingsHolder _settings;
    private readonly PipelineStages _stages;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly Lock _lock = new();

    private bool _started;
    private bool _paused;
    private bool _exclusiveFullscreen;
    private PipelineState _state = new(PipelinePlan.AllStopped, false, false, null);

    /// <summary>Creates the orchestrator. Nothing runs until <see cref="Start"/>.</summary>
    public PipelineOrchestrator(SettingsHolder settings, PipelineStages stages, ILogger<PipelineOrchestrator> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Raised after a reconciliation changed the state, on the thread that caused it and outside any lock.
    /// Several threads may raise it concurrently, so handlers should read <see cref="State"/> (always the
    /// latest) when they get to run, rather than trusting the order of the event arguments.
    /// </summary>
    public event EventHandler<PipelineState>? StateChanged;

    /// <summary>The latest state. Safe from any thread.</summary>
    public PipelineState State => Volatile.Read(ref _state);

    /// <summary>Starts following the settings and runs the stages the current plan asks for.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _settings.Published += OnSettingsPublished;
        }

        Reconcile(() => _started = true);
    }

    /// <summary>Stops every stage (producers first) and stops following the settings. Safe to call repeatedly.</summary>
    public void Stop()
    {
        _settings.Published -= OnSettingsPublished;
        Reconcile(() => _started = false);
    }

    /// <summary>Pauses or resumes the whole effect. A paused pipeline has no thread running and holds no GPU resources.</summary>
    public void SetPaused(bool paused) => Reconcile(() => _paused = paused);

    /// <summary>Flips the pause state and returns the new value.</summary>
    public bool TogglePause()
    {
        var paused = false;
        Reconcile(() => paused = _paused = !_paused);
        return paused;
    }

    /// <summary>Reports whether an exclusive-fullscreen application is in front (from the fullscreen monitor).</summary>
    public void SetExclusiveFullscreen(bool active) => Reconcile(() => _exclusiveFullscreen = active);

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void OnSettingsPublished(object? sender, SettingsSnapshot snapshot) => Reconcile(static () => { });

    private void Reconcile(Action mutate)
    {
        PipelineState? changed = null;
        lock (_lock)
        {
            mutate();
            var next = ReconcileLocked();
            if (next != _state)
            {
                Volatile.Write(ref _state, next);
                changed = next;
            }
        }

        if (changed is not null)
        {
            HostLog.PipelineStateChanged(_logger, changed.Plan.Mode, changed.Plan.Capture, changed.Plan.Overlay, changed.Plan.Serial, changed.Plan.OverlaySuspended);
            StateChanged?.Invoke(this, changed);
        }
    }

    private PipelineState ReconcileLocked()
    {
        var plan = _started
            ? PipelinePlan.Create(_settings.Current.Settings, _paused, _exclusiveFullscreen)
            : PipelinePlan.AllStopped;

        string? error = null;
        foreach (var (stage, wanted) in _stages.InStopOrder(plan))
        {
            if (stage.IsRunning && !wanted)
            {
                error ??= TryStop(stage);
            }
        }

        foreach (var (stage, wanted) in _stages.InStartOrder(plan))
        {
            if (!stage.IsRunning && wanted)
            {
                error ??= TryStart(stage);
            }
        }

        return new PipelineState(plan, _paused, _exclusiveFullscreen, error);
    }

    private string? TryStart(IPipelineStage stage)
    {
        try
        {
            stage.StartStage();
            HostLog.StageStarted(_logger, stage.Name);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HostLog.StageStartFailed(_logger, stage.Name, exception);
            return $"{stage.Name} could not start: {exception.Message}";
        }
    }

    private string? TryStop(IPipelineStage stage)
    {
        try
        {
            stage.StopStage();
            HostLog.StageStopped(_logger, stage.Name);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HostLog.StageStopFailed(_logger, stage.Name, exception);
            return $"{stage.Name} did not stop: {exception.Message}";
        }
    }
}
