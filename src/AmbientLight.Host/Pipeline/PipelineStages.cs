namespace AmbientLight.Host.Pipeline;

/// <summary>One startable/stoppable part of the pipeline, as seen by <see cref="PipelineOrchestrator"/>.</summary>
/// <remarks>Only the orchestrator calls these members, always under its lock, so implementations need no locking.</remarks>
public interface IPipelineStage
{
    /// <summary>Short name for logs.</summary>
    string Name { get; }

    /// <summary>True between a successful <see cref="StartStage"/> and a successful <see cref="StopStage"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the stage. Throws when it cannot start; the stage then stays stopped.</summary>
    void StartStage();

    /// <summary>Stops the stage. Throws (typically <see cref="TimeoutException"/>) when it did not stop; it then still counts as running.</summary>
    void StopStage();
}

/// <summary>An <see cref="IPipelineStage"/> made of two delegates, tracking whether it runs.</summary>
/// <param name="name">Name for logs.</param>
/// <param name="start">Starts the underlying service.</param>
/// <param name="stop">Stops the underlying service.</param>
public sealed class DelegatingStage(string name, Action start, Action stop) : IPipelineStage
{
    private readonly Action _start = start ?? throw new ArgumentNullException(nameof(start));
    private readonly Action _stop = stop ?? throw new ArgumentNullException(nameof(stop));

    /// <inheritdoc />
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public void StartStage()
    {
        if (IsRunning)
        {
            return;
        }

        _start();
        IsRunning = true;
    }

    /// <inheritdoc />
    public void StopStage()
    {
        if (!IsRunning)
        {
            return;
        }

        _stop();
        IsRunning = false;
    }
}

/// <summary>The four pipeline stages in data-flow order.</summary>
public sealed class PipelineStages
{
    /// <summary>Groups the stages.</summary>
    public PipelineStages(IPipelineStage capture, IPipelineStage processing, IPipelineStage overlay, IPipelineStage serial)
    {
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Processing = processing ?? throw new ArgumentNullException(nameof(processing));
        Overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        Serial = serial ?? throw new ArgumentNullException(nameof(serial));
    }

    /// <summary>T1: Desktop Duplication + GPU zone reduction.</summary>
    public IPipelineStage Capture { get; }

    /// <summary>T2: color pipeline.</summary>
    public IPipelineStage Processing { get; }

    /// <summary>T3: glow overlay.</summary>
    public IPipelineStage Overlay { get; }

    /// <summary>T4: LED strip output.</summary>
    public IPipelineStage Serial { get; }

    /// <summary>
    /// Stop order: producers first, so no stage is left publishing into a mailbox whose reader has gone and the
    /// LED strip's blackout frame is the last thing it receives.
    /// </summary>
    internal IEnumerable<(IPipelineStage Stage, bool Wanted)> InStopOrder(PipelinePlan plan)
    {
        yield return (Capture, plan.Capture);
        yield return (Processing, plan.Processing);
        yield return (Overlay, plan.Overlay);
        yield return (Serial, plan.Serial);
    }

    /// <summary>Start order: consumers first, so the very first processed frame already has a reader.</summary>
    internal IEnumerable<(IPipelineStage Stage, bool Wanted)> InStartOrder(PipelinePlan plan)
    {
        yield return (Serial, plan.Serial);
        yield return (Overlay, plan.Overlay);
        yield return (Processing, plan.Processing);
        yield return (Capture, plan.Capture);
    }
}
