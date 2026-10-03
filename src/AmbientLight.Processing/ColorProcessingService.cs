using System.Diagnostics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Core.Zones;
using AmbientLight.Processing.Pipeline;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Processing;

/// <summary>Point-in-time counters of the processing stage.</summary>
/// <param name="FramesIngested">Captures taken from the capture stage.</param>
/// <param name="FramesPublished">Frames handed to the overlay and serial stages (includes smoothing ticks).</param>
/// <param name="PowerLimitedFrames">Published frames the power limiter had to dim.</param>
/// <param name="LastProcessingTime">CPU time of the most recent ingest + render.</param>
/// <param name="ContentBounds">Content area currently fed back to the capture stage.</param>
public readonly record struct ProcessingStatistics(
    long FramesIngested,
    long FramesPublished,
    long PowerLimitedFrames,
    TimeSpan LastProcessingTime,
    NormalizedRect ContentBounds);

/// <summary>
/// Pipeline stage T2: runs <see cref="ColorPipeline"/> on a dedicated thread between the capture stage
/// and the output stages.
/// </summary>
/// <remarks>
/// <para>The thread sleeps on the input mailbox and wakes when:</para>
/// <list type="bullet">
/// <item>a new capture arrives: ingest, render, publish;</item>
/// <item>smoothing has not converged: it ticks once per capture frame interval even if the desktop is
/// static, so a fade completes on screen;</item>
/// <item>the settings changed: it re-renders so brightness or color changes apply on a static screen.</item>
/// </list>
/// <para>
/// When idle and converged it blocks for up to <see cref="IdleWait"/> per loop, which costs nothing.
/// Content bounds from letterbox detection are published to a <see cref="SnapshotCell{T}"/> read by the
/// capture stage; that publish allocates only when the bars actually change.
/// </para>
/// </remarks>
public sealed class ColorProcessingService : IDisposable
{
    private const string ThreadName = "AmbientLight.Processing";

    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsHolder _settings;
    private readonly LatestValueMailbox<ZoneSampleFrame> _input;
    private readonly LatestValueBroadcaster<FrameData> _output;
    private readonly SnapshotCell<NormalizedRect> _contentBounds;
    private readonly ILogger<ColorProcessingService> _logger;
    private readonly ColorPipeline _pipeline = new(LedLayoutSettings.MaxLedCount);
    private readonly FrameData _scratch = new(LedLayoutSettings.MaxLedCount);
    private readonly Lock _lifecycleLock = new();

    private Thread? _thread;
    private volatile bool _stopRequested;

    private long _framesIngested;
    private long _framesPublished;
    private long _powerLimitedFrames;
    private long _lastProcessingTicks;

    /// <summary>Creates the service. Nothing runs until <see cref="Start"/>.</summary>
    /// <param name="settings">Configuration source.</param>
    /// <param name="input">Mailbox filled by the capture stage; this service is its only consumer.</param>
    /// <param name="output">Fan-out to the overlay and serial stages; this service is its only producer.</param>
    /// <param name="contentBounds">Letterbox feedback read by the capture stage; this service is its only writer.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public ColorProcessingService(
        SettingsHolder settings,
        LatestValueMailbox<ZoneSampleFrame> input,
        LatestValueBroadcaster<FrameData> output,
        SnapshotCell<NormalizedRect> contentBounds,
        ILogger<ColorProcessingService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _contentBounds = contentBounds ?? throw new ArgumentNullException(nameof(contentBounds));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True while the processing thread runs.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _thread is not null;
            }
        }
    }

    /// <summary>Starts the processing thread.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Processing is already running.");
            }

            _stopRequested = false;
            _thread = new Thread(RunProcessingLoop)
            {
                Name = ThreadName,
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>Stops the processing thread. Safe to call repeatedly.</summary>
    /// <exception cref="TimeoutException">The thread did not exit in time.</exception>
    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            thread = _thread;
            if (thread is null)
            {
                return;
            }

            _stopRequested = true;
            _input.Wake();
        }

        if (!thread.Join(StopTimeout))
        {
            throw new TimeoutException($"The processing thread did not stop within {StopTimeout.TotalSeconds} s.");
        }

        lock (_lifecycleLock)
        {
            _thread = null;
        }
    }

    /// <summary>Returns a snapshot of the counters.</summary>
    public ProcessingStatistics GetStatistics() => new(
        Interlocked.Read(ref _framesIngested),
        Interlocked.Read(ref _framesPublished),
        Interlocked.Read(ref _powerLimitedFrames),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastProcessingTicks)),
        _contentBounds.Current.Value);

    /// <summary>Stops processing; a thread that does not stop in time is abandoned (it is a background thread).</summary>
    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            // Nothing else can be done for a stuck thread; it is a background thread and dies with the process.
            return;
        }
    }

    private void RunProcessingLoop()
    {
        ProcessingLog.Started(_logger);
        var transitioning = false;
        long renderedSettingsVersion = -1;

        try
        {
            while (!_stopRequested)
            {
                var snapshot = _settings.Current;
                var wait = transitioning
                    ? TimeSpan.FromSeconds(1.0 / snapshot.Settings.Capture.MaxFps)
                    : IdleWait;

                var hasNewFrame = _input.WaitAndAcquireLatest(wait);
                if (_stopRequested)
                {
                    break;
                }

                snapshot = _settings.Current;
                var settingsChanged = snapshot.Version != renderedSettingsVersion;
                if (!hasNewFrame && !transitioning && !settingsChanged)
                {
                    continue;
                }

                try
                {
                    transitioning = ProcessOnce(snapshot, hasNewFrame);
                    renderedSettingsVersion = snapshot.Version;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    // A bug or an inconsistent frame must not kill the stage; log it and carry on.
                    ProcessingLog.Faulted(_logger, exception);
                    transitioning = false;
                    Thread.Sleep(FaultBackoff);
                }
            }
        }
        finally
        {
            ProcessingLog.Stopped(_logger);
        }
    }

    private bool ProcessOnce(SettingsSnapshot snapshot, bool hasNewFrame)
    {
        var started = Stopwatch.GetTimestamp();

        if (hasNewFrame)
        {
            Interlocked.Increment(ref _framesIngested);
            if (_pipeline.Ingest(_input.ReadSlot, snapshot, started))
            {
                ProcessingLog.ContentBoundsChanged(_logger, _pipeline.DetectedContentBounds);
            }

            // No-op (and no allocation) unless the bounds actually changed.
            _contentBounds.Publish(_pipeline.DetectedContentBounds);
        }

        if (!_pipeline.HasTarget)
        {
            return false;
        }

        var transitioning = _pipeline.Render(Stopwatch.GetTimestamp(), snapshot, _scratch);
        _output.Publish(_scratch);

        Interlocked.Increment(ref _framesPublished);
        if (_scratch.PowerLimitScale < 1f)
        {
            Interlocked.Increment(ref _powerLimitedFrames);
        }

        Interlocked.Exchange(ref _lastProcessingTicks, Stopwatch.GetTimestamp() - started);
        return transitioning;
    }
}
