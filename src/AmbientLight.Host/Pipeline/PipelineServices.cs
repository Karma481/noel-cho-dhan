using AmbientLight.Capture;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Core.Zones;
using AmbientLight.Overlay;
using AmbientLight.Processing;
using AmbientLight.Serial;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Host.Pipeline;

/// <summary>Point-in-time view of every stage, for the settings window's status footer.</summary>
/// <param name="CaptureStatus">Capture lifecycle state.</param>
/// <param name="Capture">Capture counters.</param>
/// <param name="CapturedOutput">The monitor and GPU being captured, once capture has started.</param>
/// <param name="ProcessingRunning">The processing thread runs.</param>
/// <param name="Processing">Processing counters.</param>
/// <param name="OverlayStatus">Overlay lifecycle state.</param>
/// <param name="Overlay">Overlay counters, including the capture-exclusion result.</param>
/// <param name="Serial">LED output counters, or <see langword="null"/> while the LED output was never started.</param>
public sealed record PipelineStatus(
    CaptureStatus CaptureStatus,
    CaptureStatistics Capture,
    DisplayOutputInfo? CapturedOutput,
    bool ProcessingRunning,
    ProcessingStatistics Processing,
    OverlayStatus OverlayStatus,
    OverlayStatistics Overlay,
    SerialOutputStatistics? Serial);

/// <summary>
/// The real pipeline: the hand-off buffers between the stages, the four services, and their
/// <see cref="PipelineStages"/> adapters for the orchestrator.
/// </summary>
/// <remarks>
/// <code>
///  T1 Capture ──LatestValueMailbox&lt;ZoneSampleFrame&gt;──▶ T2 Processing ──LatestValueBroadcaster&lt;FrameData&gt;─┬─[0]─▶ T3 Overlay
///       ▲                                                   │                                         └─[1]─▶ T4 Serial
///       └──────────────SnapshotCell&lt;NormalizedRect&gt; (letterbox bounds)───┘
/// </code>
/// <list type="bullet">
/// <item>The serial service is created on its first start. With the LED strip disabled (the default) there is
/// no serial object, no thread and no port handle at all.</item>
/// <item>A broadcaster subscriber is enabled only while its stage runs, so processing never copies frames
/// for an output that is stopped (for example the overlay during exclusive fullscreen).</item>
/// <item>All buffers are allocated once here, sized for <see cref="LedLayoutSettings.MaxLedCount"/>; nothing is
/// allocated per frame afterwards.</item>
/// </list>
/// </remarks>
public sealed class PipelineServices : IDisposable
{
    /// <summary>Broadcaster subscriber index of the overlay.</summary>
    public const int OverlaySubscriber = 0;

    /// <summary>Broadcaster subscriber index of the LED output.</summary>
    public const int SerialSubscriber = 1;

    private readonly SettingsHolder _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LatestValueMailbox<ZoneSampleFrame> _samples;
    private readonly LatestValueBroadcaster<FrameData> _frames;
    private SerialOutputService? _serial;
    private bool _disposed;

    /// <summary>Creates the buffers and services. No thread starts and no GPU object is created here.</summary>
    public PipelineServices(SettingsHolder settings, ILoggerFactory loggerFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _samples = new LatestValueMailbox<ZoneSampleFrame>(static () => new ZoneSampleFrame(LedLayoutSettings.MaxLedCount));
        _frames = new LatestValueBroadcaster<FrameData>(2, static () => new FrameData(LedLayoutSettings.MaxLedCount));
        _frames.SetSubscriberEnabled(OverlaySubscriber, false);
        _frames.SetSubscriberEnabled(SerialSubscriber, false);
        var contentBounds = new SnapshotCell<NormalizedRect>(NormalizedRect.Full);

        Capture = new DesktopCaptureService(settings, _samples, contentBounds, loggerFactory.CreateLogger<DesktopCaptureService>());
        Processing = new ColorProcessingService(settings, _samples, _frames, contentBounds, loggerFactory.CreateLogger<ColorProcessingService>());
        Overlay = new OverlayService(settings, _frames.GetSubscriber(OverlaySubscriber), loggerFactory.CreateLogger<OverlayService>());

        Stages = new PipelineStages(
            new DelegatingStage("Capture", Capture.Start, Capture.Stop),
            new DelegatingStage("Processing", Processing.Start, Processing.Stop),
            new DelegatingStage("Overlay", StartOverlay, StopOverlay),
            new DelegatingStage("Serial", StartSerial, StopSerial));
    }

    /// <summary>Stage T1.</summary>
    public DesktopCaptureService Capture { get; }

    /// <summary>Stage T2.</summary>
    public ColorProcessingService Processing { get; }

    /// <summary>Stage T3.</summary>
    public OverlayService Overlay { get; }

    /// <summary>Stage T4, or <see langword="null"/> until the LED output is first enabled.</summary>
    public SerialOutputService? Serial => Volatile.Read(ref _serial);

    /// <summary>Adapters handed to <see cref="PipelineOrchestrator"/>.</summary>
    public PipelineStages Stages { get; }

    /// <summary>Collects every stage's status. Safe from any thread; allocates, so call it at UI rates only.</summary>
    public PipelineStatus GetStatus() => new(
        Capture.Status,
        Capture.GetStatistics(),
        Capture.CurrentOutput,
        Processing.IsRunning,
        Processing.GetStatistics(),
        Overlay.Status,
        Overlay.GetStatistics(),
        Serial?.GetStatistics());

    /// <summary>
    /// Disposes the services and buffers. The orchestrator must have stopped the stages first; services still
    /// running are stopped by their own Dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Capture.Dispose();
        Processing.Dispose();
        Overlay.Dispose();
        _serial?.Dispose();
        _frames.Dispose();
        _samples.Dispose();
    }

    private void StartOverlay()
    {
        // Enable the subscriber first so the first frame published after the thread starts is not skipped.
        _frames.SetSubscriberEnabled(OverlaySubscriber, true);
        try
        {
            Overlay.Start();
        }
        catch
        {
            _frames.SetSubscriberEnabled(OverlaySubscriber, false);
            throw;
        }
    }

    private void StopOverlay()
    {
        _frames.SetSubscriberEnabled(OverlaySubscriber, false);
        Overlay.Stop();
    }

    private void StartSerial()
    {
        var serial = _serial;
        if (serial is null)
        {
            serial = new SerialOutputService(_settings, _frames.GetSubscriber(SerialSubscriber), _loggerFactory.CreateLogger<SerialOutputService>());
            Volatile.Write(ref _serial, serial);
        }

        _frames.SetSubscriberEnabled(SerialSubscriber, true);
        try
        {
            serial.Start();
        }
        catch
        {
            _frames.SetSubscriberEnabled(SerialSubscriber, false);
            throw;
        }
    }

    private void StopSerial()
    {
        _frames.SetSubscriberEnabled(SerialSubscriber, false);
        _serial?.Stop();
    }
}
