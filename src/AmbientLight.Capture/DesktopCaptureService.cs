using System.Diagnostics;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Capture.Duplication;
using AmbientLight.Capture.Gpu;
using AmbientLight.Capture.Interop;
using AmbientLight.Capture.Recovery;
using AmbientLight.Capture.Threading;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Core.Zones;
using Microsoft.Extensions.Logging;
using Vortice.DXGI;

namespace AmbientLight.Capture;

/// <summary>
/// Pipeline stage T1: captures the desktop with DXGI Desktop Duplication, reduces it to one
/// linear-light color per zone on the GPU, and publishes <see cref="ZoneSampleFrame"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Runs on one dedicated thread that owns every D3D/DXGI object it uses, so no D3D call is ever made
/// concurrently and no lock is needed. Output goes through a <see cref="LatestValueMailbox{T}"/>: publishing
/// is wait-free, so a slow consumer can never stall capture.
/// </para>
/// <para>Failure handling is tiered (see <see cref="CaptureRecovery"/>):</para>
/// <list type="bullet">
/// <item><b>Timeout</b> (static screen): normal; nothing is published, consumers keep the last colors.</item>
/// <item><b>Access lost</b> (mode/HDR/rotation change, fullscreen transition): recreate only the duplication, immediately.</item>
/// <item><b>Secure desktop / session disconnect</b>: wait with back-off, then recreate the duplication.</item>
/// <item><b>Device removed / output gone</b>: rebuild device, reducer and duplication from scratch.</item>
/// <item><b>Repeated access lost</b>: escalated to a device rebuild in case the output itself changed.</item>
/// </list>
/// </remarks>
public sealed class DesktopCaptureService : IDisposable
{
    private const string MmcssTaskName = "Capture";
    private const string ThreadName = "AmbientLight.Capture";
    private const int EscalationThreshold = 5;

    /// <summary>
    /// Subtracted from the MaxFps interval so that, when the monitor refresh equals the cap, the wait ends
    /// just before the next present instead of just after it (which would add up to a frame of latency).
    /// </summary>
    private static readonly long ThrottleSlackTicks = Stopwatch.Frequency / 1000;

    private static readonly TimeSpan OutputInfoRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsHolder _settings;
    private readonly LatestValueMailbox<ZoneSampleFrame> _output;
    private readonly SnapshotCell<NormalizedRect> _contentBounds;
    private readonly ILogger<DesktopCaptureService> _logger;
    private readonly ManualResetEventSlim _stopSignal = new(initialState: false);
    private readonly Lock _lifecycleLock = new();
    private readonly ZoneConfig[] _textureZones = new ZoneConfig[LedLayoutSettings.MaxLedCount];

    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _status;
    private DisplayOutputInfo? _currentOutput;

    // Capture-thread state.
    private CaptureDevice? _device;
    private DesktopDuplicator? _duplicator;
    private GpuZoneReducer? _reducer;
    private CaptureSettings? _appliedCapture;
    private LedLayoutSettings? _appliedLayout;
    private LetterboxSettings? _appliedLetterbox;
    private long _uploadedZonesVersion = -1;
    private ModeRotation _uploadedRotation;
    private NormalizedRect _uploadedContent = NormalizedRect.Full;
    private bool _forceProcess;
    private long _lastProcessedTicks;
    private long _lastOutputRefreshTicks;
    private long _sequence;

    // Statistics: written by the capture thread, read from any thread.
    private long _framesAcquired;
    private long _framesPublished;
    private long _pointerOnlyFrames;
    private long _timeouts;
    private long _duplicationRecreations;
    private long _deviceRecreations;
    private long _lastGpuReduceTicks;
    private int _protectedContentMasked;
    private int _usesLegacyDuplication;
    private int _encoding;

    /// <summary>Creates the service. Nothing touches the GPU until <see cref="Start"/>.</summary>
    /// <param name="settings">Configuration source, read once per captured frame.</param>
    /// <param name="output">
    /// Mailbox read by the processing stage. Its frames must hold <see cref="LedLayoutSettings.MaxLedCount"/>
    /// zones; this service is its only producer.
    /// </param>
    /// <param name="contentBounds">
    /// Picture area between black bars, written by the processing stage's letterbox detector. Zones are
    /// sampled within it while letterbox detection is enabled.
    /// </param>
    /// <param name="logger">Diagnostics sink.</param>
    public DesktopCaptureService(
        SettingsHolder settings,
        LatestValueMailbox<ZoneSampleFrame> output,
        SnapshotCell<NormalizedRect> contentBounds,
        ILogger<DesktopCaptureService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _contentBounds = contentBounds ?? throw new ArgumentNullException(nameof(contentBounds));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current lifecycle state.</summary>
    public CaptureStatus Status => (CaptureStatus)Volatile.Read(ref _status);

    /// <summary>The monitor being captured, or <see langword="null"/> before the first successful start.</summary>
    public DisplayOutputInfo? CurrentOutput => Volatile.Read(ref _currentOutput);

    /// <summary>Starts the capture thread.</summary>
    /// <exception cref="PlatformNotSupportedException">Windows older than 10 version 2004.</exception>
    /// <exception cref="InvalidOperationException">Already started, or the mailbox frames are too small.</exception>
    public void Start()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException("Desktop capture requires Windows 10 version 2004 (build 19041) or later.");
        }

        lock (_lifecycleLock)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            // Safe before the thread starts: the mailbox has no producer yet.
            if (_output.WriteSlot.Capacity < LedLayoutSettings.MaxLedCount)
            {
                throw new InvalidOperationException(
                    $"Mailbox frames hold {_output.WriteSlot.Capacity} zones; at least {LedLayoutSettings.MaxLedCount} are required.");
            }

            _stopRequested = false;
            _stopSignal.Reset();
            SetStatus(CaptureStatus.Starting);

            _thread = new Thread(RunCaptureLoop)
            {
                Name = ThreadName,
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>Stops the capture thread and releases every GPU resource. Safe to call repeatedly.</summary>
    /// <exception cref="TimeoutException">The thread did not exit in time (a driver call is hung).</exception>
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
            _stopSignal.Set();
        }

        if (!thread.Join(StopTimeout))
        {
            throw new TimeoutException($"The capture thread did not stop within {StopTimeout.TotalSeconds} s.");
        }

        lock (_lifecycleLock)
        {
            _thread = null;
        }
    }

    /// <summary>Returns a consistent-enough snapshot of the counters for display.</summary>
    public CaptureStatistics GetStatistics() => new(
        Interlocked.Read(ref _framesAcquired),
        Interlocked.Read(ref _framesPublished),
        Interlocked.Read(ref _pointerOnlyFrames),
        Interlocked.Read(ref _timeouts),
        Interlocked.Read(ref _duplicationRecreations),
        Interlocked.Read(ref _deviceRecreations),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastGpuReduceTicks)),
        Volatile.Read(ref _protectedContentMasked) != 0,
        Volatile.Read(ref _usesLegacyDuplication) != 0,
        (SurfaceEncoding)Volatile.Read(ref _encoding));

    /// <summary>
    /// Stops capture and releases the stop event. If the thread is stuck inside a driver call it is
    /// abandoned (it is a background thread) and the event is left for it to observe.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            return;
        }

        _stopSignal.Dispose();
    }

    private void RunCaptureLoop()
    {
        using var mmcss = MmcssRegistration.TryRegisterCurrentThread(MmcssTaskName);
        if (mmcss is null)
        {
            CaptureLog.MmcssUnavailable(_logger);
        }

        if (ThreadingInterop.SetThreadDpiAwarenessContext(ThreadingInterop.DpiAwarenessContextPerMonitorAwareV2) == IntPtr.Zero)
        {
            CaptureLog.DpiAwarenessUnavailable(_logger);
        }

        using var timer = new PrecisionTimer();
        var backoff = new RecoveryBackoff();

        try
        {
            while (!_stopRequested)
            {
                try
                {
                    CaptureOnce(_settings.Current, timer, backoff);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Recover(exception, backoff);
                }
            }
        }
        finally
        {
            ReleaseGpuResources();
            SetStatus(CaptureStatus.Stopped);
            CaptureLog.Stopped(_logger);
        }
    }

    private void CaptureOnce(SettingsSnapshot snapshot, PrecisionTimer timer, RecoveryBackoff backoff)
    {
        var capture = snapshot.Settings.Capture;
        EnsureDevice(capture);
        EnsureDuplication();
        RestartIfCaptureSettingsChanged(snapshot);

        ThrottleToMaxFps(capture.MaxFps, timer);
        if (_stopRequested)
        {
            return;
        }

        var outcome = _duplicator!.TryAcquire(capture.AcquireTimeoutMs, out var frame);
        var acquiredTicks = Stopwatch.GetTimestamp();
        backoff.Reset();
        SetStatus(CaptureStatus.Capturing);

        if (outcome == AcquireOutcome.Timeout)
        {
            Interlocked.Increment(ref _timeouts);
            RefreshOutputInfoIfDue();
            return;
        }

        Interlocked.Increment(ref _framesAcquired);
        Volatile.Write(ref _protectedContentMasked, frame.ProtectedContentMaskedOut ? 1 : 0);

        if (!frame.HasNewImage && !_forceProcess)
        {
            Interlocked.Increment(ref _pointerOnlyFrames);
            return;
        }

        ReduceAndPublish(snapshot, frame);
        _forceProcess = false;
        _lastProcessedTicks = acquiredTicks;
        RefreshOutputInfoIfDue();
    }

    private void EnsureDevice(CaptureSettings capture)
    {
        if (_device is not null &&
            !string.Equals(_device.RequestedOutputName, capture.OutputDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            // The user picked another monitor: it may be on another adapter, so rebuild everything.
            ReleaseGpuResources();
        }

        if (_device is not null)
        {
            return;
        }

        SetStatus(CaptureStatus.Starting);

        // Build all three or none, so a failure (shader compile, out of video memory) never leaves a
        // half-initialized set behind.
        var device = CaptureDevice.Create(capture.OutputDeviceName);
        GpuZoneReducer? reducer = null;
        try
        {
            reducer = new GpuZoneReducer(device, LedLayoutSettings.MaxLedCount);
        }
        catch
        {
            reducer?.Dispose();
            device.Dispose();
            throw;
        }

        _device = device;
        _reducer = reducer;
        _duplicator = new DesktopDuplicator(device);
        CaptureLog.GpuSynchronization(_logger, reducer.UsesFence ? "ID3D11Fence + event wait" : "blocking Map (no fence support)");
    }

    private void EnsureDuplication()
    {
        if (_duplicator!.IsActive)
        {
            return;
        }

        _duplicator.Start();
        OnDuplicationStarted();
    }

    private void OnDuplicationStarted()
    {
        var info = _device!.OutputInfo;
        Volatile.Write(ref _currentOutput, info);
        Volatile.Write(ref _usesLegacyDuplication, _duplicator!.UsesLegacyApi ? 1 : 0);
        if (_duplicator.UsesLegacyApi)
        {
            CaptureLog.LegacyDuplication(_logger);
        }

        CaptureLog.CaptureStarted(
            _logger,
            info.DeviceName,
            info.DesktopWidth,
            info.DesktopHeight,
            info.Rotation,
            info.IsHdr,
            info.SdrWhiteNits,
            info.MaxLuminanceNits,
            info.AdapterName);

        // The first frame of a new duplication carries the full current image: always process it, even
        // if the screen then stays static, and re-upload zones because the rotation may have changed.
        _forceProcess = true;
        _uploadedZonesVersion = -1;
        _lastOutputRefreshTicks = Stopwatch.GetTimestamp();
        SetStatus(CaptureStatus.Capturing);
    }

    /// <summary>
    /// After a change to the capture settings or the LED layout, recreate the duplication so the next
    /// acquire returns the full current image, which is then reduced with the new parameters. Without
    /// this, a static screen would keep showing colors computed for the old layout.
    /// </summary>
    private void RestartIfCaptureSettingsChanged(SettingsSnapshot snapshot)
    {
        var capture = snapshot.Settings.Capture;
        var layout = snapshot.Settings.LedLayout;
        var letterbox = snapshot.Settings.Letterbox;
        var firstApplication = _appliedCapture is null;
        var changed = capture != _appliedCapture || layout != _appliedLayout || letterbox != _appliedLetterbox;

        _appliedCapture = capture;
        _appliedLayout = layout;
        _appliedLetterbox = letterbox;

        if (changed && !firstApplication)
        {
            _duplicator!.Start();
            OnDuplicationStarted();
        }
    }

    private void ThrottleToMaxFps(int maxFps, PrecisionTimer timer)
    {
        if (_lastProcessedTicks == 0 || maxFps <= 0)
        {
            return;
        }

        // The deadline is anchored at the previous acquire (close to its present time). Waiting here, while
        // the previous frame is still held, lets DWM accumulate every update into the next frame, so the
        // next acquire returns immediately with the freshest image.
        var interval = Math.Max(0, (Stopwatch.Frequency / maxFps) - ThrottleSlackTicks);
        timer.WaitUntil(_lastProcessedTicks + interval);
    }

    private void ReduceAndPublish(SettingsSnapshot snapshot, AcquiredFrame frame)
    {
        var capture = snapshot.Settings.Capture;
        var letterboxEnabled = snapshot.Settings.Letterbox.Enabled;
        var zones = snapshot.Zones;
        var view = _duplicator!.GetSampleableView(out var surface);
        var rotation = _duplicator.Description.Rotation;

        // One volatile read; the cell only changes when the detector confirms different bars.
        var content = letterboxEnabled ? _contentBounds.Current.Value : NormalizedRect.Full;
        if (!content.IsValid)
        {
            content = NormalizedRect.Full;
        }

        if (_uploadedZonesVersion != snapshot.Version || _uploadedRotation != rotation || _uploadedContent != content)
        {
            SurfaceOrientation.TransformZones(zones.AsSpan(), content, rotation, _textureZones);
            _reducer!.UploadZones(_textureZones.AsSpan(0, zones.Length));
            _uploadedZonesVersion = snapshot.Version;
            _uploadedRotation = rotation;
            _uploadedContent = content;
        }

        var output = _device!.OutputInfo;
        var mapping = ColorSpaceMapping.Resolve(surface.Format, output, capture);
        var constants = ZoneReduceConstants.Create(zones.Length, capture.SamplesPerZoneAxis, mapping);

        var slot = _output.WriteSlot;
        slot.SetZoneCount(zones.Length);
        var profile = letterboxEnabled ? slot.LineLuma : Span<float>.Empty;
        var gpuTime = _reducer!.Reduce(view, constants, slot.Samples, EdgeProfileConstants.For(rotation), profile);

        var (width, height) = SurfaceOrientation.VisibleSize((int)surface.Width, (int)surface.Height, rotation);
        slot.Sequence = ++_sequence;
        slot.LayoutVersion = snapshot.Version;
        slot.Timing = new FrameTiming(frame.LastPresentTime, Stopwatch.GetTimestamp(), Processed: 0);
        slot.SourceWidth = width;
        slot.SourceHeight = height;
        slot.IsHdr = mapping.Encoding == SurfaceEncoding.ScRgbLinear && output.IsHdr;
        slot.ContentBounds = content;
        slot.HasProfile = letterboxEnabled;
        _output.Publish();

        Interlocked.Increment(ref _framesPublished);
        Interlocked.Exchange(ref _lastGpuReduceTicks, (long)(gpuTime.TotalSeconds * Stopwatch.Frequency));
        Volatile.Write(ref _encoding, (int)mapping.Encoding);
    }

    /// <summary>
    /// The SDR content brightness slider changes the scRGB value of SDR white without any access-lost
    /// event, so it is polled. The call is a handful of syscalls every two seconds.
    /// </summary>
    private void RefreshOutputInfoIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastOutputRefreshTicks, now) < OutputInfoRefreshInterval)
        {
            return;
        }

        _lastOutputRefreshTicks = now;
        Volatile.Write(ref _currentOutput, _device!.RefreshOutputInfo());
    }

    private void Recover(Exception exception, RecoveryBackoff backoff)
    {
        var recovery = DxgiErrorClassifier.Classify(exception);
        if (recovery == CaptureRecovery.RecreateDuplication && backoff.ConsecutiveFailures >= EscalationThreshold)
        {
            recovery = CaptureRecovery.RecreateDevice;
        }

        switch (recovery)
        {
            case CaptureRecovery.None:
                return;

            case CaptureRecovery.RecreateDuplication:
                _duplicator?.Stop();
                Interlocked.Increment(ref _duplicationRecreations);
                SetStatus(CaptureStatus.Recovering);
                break;

            case CaptureRecovery.WaitThenRecreateDuplication:
                _duplicator?.Stop();
                Interlocked.Increment(ref _duplicationRecreations);
                SetStatus(CaptureStatus.WaitingForDesktop);
                break;

            case CaptureRecovery.RecreateDevice:
                ReleaseGpuResources();
                Interlocked.Increment(ref _deviceRecreations);
                SetStatus(CaptureStatus.Recovering);
                break;

            case CaptureRecovery.Fatal:
            default:
                ReleaseGpuResources();
                SetStatus(CaptureStatus.Faulted);
                break;
        }

        var delay = backoff.NextDelay(recovery);
        if (recovery == CaptureRecovery.Fatal)
        {
            CaptureLog.Faulted(_logger, exception, recovery, delay.TotalMilliseconds);
        }
        else if (backoff.ConsecutiveFailures == 1)
        {
            CaptureLog.Recovering(_logger, recovery, exception.Message, delay.TotalMilliseconds, backoff.ConsecutiveFailures);
        }
        else
        {
            // A locked workstation can keep duplication unavailable for hours; keep repeats out of the default log.
            CaptureLog.StillRecovering(_logger, recovery, exception.Message, delay.TotalMilliseconds, backoff.ConsecutiveFailures);
        }

        if (delay > TimeSpan.Zero)
        {
            _stopSignal.Wait(delay);
        }
    }

    private void ReleaseGpuResources()
    {
        _duplicator?.Dispose();
        _duplicator = null;
        _reducer?.Dispose();
        _reducer = null;
        _device?.Dispose();
        _device = null;

        _appliedCapture = null;
        _appliedLayout = null;
        _appliedLetterbox = null;
        _uploadedZonesVersion = -1;
        _lastProcessedTicks = 0;
    }

    private void SetStatus(CaptureStatus status) => Volatile.Write(ref _status, (int)status);
}
