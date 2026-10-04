using System.ComponentModel;
using System.Diagnostics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Overlay.Interop;
using AmbientLight.Overlay.Rendering;
using AmbientLight.Overlay.Window;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;

namespace AmbientLight.Overlay;

/// <summary>Lifecycle state of <see cref="OverlayService"/>.</summary>
public enum OverlayStatus
{
    /// <summary>The overlay thread is not running.</summary>
    Stopped = 0,

    /// <summary>Running; the glow is shown (or about to be, after the first frame).</summary>
    Running = 1,

    /// <summary>Turned off in the settings; window hidden, GPU resources released.</summary>
    Disabled = 2,

    /// <summary>The window cannot be excluded from capture, so it is kept hidden. See <see cref="CaptureExclusion"/>.</summary>
    CaptureExclusionUnavailable = 3,

    /// <summary>The configured monitor is not attached; waiting for it.</summary>
    MonitorNotFound = 4,

    /// <summary>Rebuilding the rendering device after it was lost.</summary>
    Recovering = 5,

    /// <summary>An unexpected error occurred; retried every few seconds.</summary>
    Faulted = 6,
}

/// <summary>Point-in-time counters of the overlay stage.</summary>
/// <param name="FramesReceived">Frames taken from the processing stage.</param>
/// <param name="Redraws">Frames actually rendered and presented.</param>
/// <param name="UnchangedFrames">Received frames skipped because nothing visible changed.</param>
/// <param name="DeviceRecreations">Rendering device rebuilds after device loss.</param>
/// <param name="LastRenderTime">CPU time of the most recent redraw (draw + blur + present submission).</param>
/// <param name="Exclusion">Capture exclusion result of the current window, if one exists.</param>
/// <param name="MonitorDeviceName">Monitor the overlay currently covers.</param>
public readonly record struct OverlayStatistics(
    long FramesReceived,
    long Redraws,
    long UnchangedFrames,
    long DeviceRecreations,
    TimeSpan LastRenderTime,
    CaptureExclusion? Exclusion,
    string? MonitorDeviceName);

/// <summary>
/// Pipeline stage T3: owns the overlay window and draws the glow from <see cref="FrameData.DisplayColors"/>.
/// </summary>
/// <remarks>
/// <para>
/// A single thread owns the window, its message loop and the renderer. It sleeps in
/// <c>MsgWaitForMultipleObjectsEx</c> on both the input mailbox's event and the window's message queue,
/// so it wakes for a new frame or a window message and costs no CPU otherwise.
/// </para>
/// <para>Each wake-up:</para>
/// <list type="number">
/// <item>re-selects the monitor after a display change or when the captured monitor setting changed;</item>
/// <item>hides the window (releasing GPU resources) when the overlay is disabled, the monitor is missing, or
/// capture exclusion is unavailable;</item>
/// <item>redraws only when <see cref="RedrawTracker"/> reports a visible change.</item>
/// </list>
/// </remarks>
public sealed class OverlayService : IDisposable
{
    private const string ThreadName = "AmbientLight.Overlay";

    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecoveryWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    /// While the configured monitor is missing there may be no window to receive WM_DISPLAYCHANGE (it is only
    /// created over an existing monitor), so monitors are re-enumerated on this period until it reappears.
    /// </summary>
    private static readonly TimeSpan MissingMonitorRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsHolder _settings;
    private readonly LatestValueMailbox<FrameData> _input;
    private readonly ILogger<OverlayService> _logger;
    private readonly Lock _lifecycleLock = new();

    private Thread? _thread;
    private volatile bool _stopRequested;
    private int _status;

    // Overlay-thread state.
    private readonly RedrawTracker _tracker = new();
    private readonly GlowSegment[] _innerSegments = new GlowSegment[GlowGeometry.MaxSegmentCount(LedLayoutSettings.MaxLedCount)];
    private readonly GlowSegment[] _washSegments = new GlowSegment[GlowGeometry.MaxSegmentCount(LedLayoutSettings.MaxLedCount)];
    private OverlayWindow? _window;
    private GlowRenderer? _renderer;
    private string? _rendererMonitor;
    private MonitorDescriptor? _monitor;
    private string? _monitorSelectedFor;
    private bool _monitorDirty = true;
    private bool _monitorMissingLogged;
    private long _monitorRetryAfterTicks;
    private GlowLayout? _configuredLayout;
    private OverlayBlendMode? _configuredBlendMode;
    private bool _hasFrame;

    // Statistics.
    private long _framesReceived;
    private long _redraws;
    private long _unchangedFrames;
    private long _deviceRecreations;
    private long _lastRenderTicks;
    private int _exclusion = -1;
    private string? _monitorName;

    /// <summary>Creates the service. Nothing runs until <see cref="Start"/>.</summary>
    /// <param name="settings">Configuration source.</param>
    /// <param name="input">Mailbox of the processing stage's broadcaster reserved for the overlay; this service is its only consumer.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public OverlayService(SettingsHolder settings, LatestValueMailbox<FrameData> input, ILogger<OverlayService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current lifecycle state.</summary>
    public OverlayStatus Status => (OverlayStatus)Volatile.Read(ref _status);

    /// <summary>Starts the overlay thread.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("The overlay is already running.");
            }

            _stopRequested = false;
            _thread = new Thread(RunOverlayLoop)
            {
                Name = ThreadName,
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>Stops the overlay thread, destroying the window and its GPU resources. Safe to call repeatedly.</summary>
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
            throw new TimeoutException($"The overlay thread did not stop within {StopTimeout.TotalSeconds} s.");
        }

        lock (_lifecycleLock)
        {
            _thread = null;
        }
    }

    /// <summary>Returns a snapshot of the counters.</summary>
    public OverlayStatistics GetStatistics()
    {
        var exclusion = Volatile.Read(ref _exclusion);
        return new OverlayStatistics(
            Interlocked.Read(ref _framesReceived),
            Interlocked.Read(ref _redraws),
            Interlocked.Read(ref _unchangedFrames),
            Interlocked.Read(ref _deviceRecreations),
            Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastRenderTicks)),
            exclusion < 0 ? null : (CaptureExclusion)exclusion,
            Volatile.Read(ref _monitorName));
    }

    /// <summary>Stops the overlay; a thread that does not stop in time is abandoned (it is a background thread).</summary>
    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            // Nothing else can be done for a thread stuck in a driver call; it dies with the process.
            return;
        }
    }

    private void RunOverlayLoop()
    {
        if (User32.SetThreadDpiAwarenessContext(User32.DpiAwarenessContextPerMonitorAwareV2) == IntPtr.Zero)
        {
            OverlayLog.DpiAwarenessUnavailable(_logger);
        }

        OverlayLog.Started(_logger);
        var wait = TimeSpan.Zero;
        try
        {
            while (!_stopRequested)
            {
                if (_window is not null)
                {
                    _window.WaitAndPump(_input.AvailableWaitHandle, wait);
                }
                else
                {
                    _input.AvailableWaitHandle.WaitOne(wait);
                }

                if (_stopRequested)
                {
                    break;
                }

                var newFrame = _input.TryAcquireLatest();
                if (newFrame)
                {
                    _hasFrame = true;
                    Interlocked.Increment(ref _framesReceived);
                }

                wait = IdleWait;
                try
                {
                    Step(_settings.Current, newFrame);
                }
                catch (OverlayDeviceLostException exception)
                {
                    OverlayLog.DeviceLost(_logger, exception);
                    ReleaseRenderer();
                    Interlocked.Increment(ref _deviceRecreations);
                    SetStatus(OverlayStatus.Recovering);
                    wait = RecoveryWait;
                }
                catch (Exception exception) when (exception is Win32Exception or SharpGenException or InvalidOperationException)
                {
                    OverlayLog.Faulted(_logger, exception, FaultBackoff.TotalMilliseconds);
                    ReleaseAll();
                    SetStatus(OverlayStatus.Faulted);
                    wait = FaultBackoff;
                }
            }
        }
        finally
        {
            ReleaseAll();
            SetStatus(OverlayStatus.Stopped);
            OverlayLog.Stopped(_logger);
        }
    }

    private void Step(SettingsSnapshot snapshot, bool newFrame)
    {
        var settings = snapshot.Settings;
        var overlay = settings.Overlay;

        if (_window?.ConsumeGeometryChanged() == true)
        {
            _monitorDirty = true;
        }

        var requestedOutput = settings.Capture.OutputDeviceName;
        var retryMissing = _monitor is null && Stopwatch.GetTimestamp() >= _monitorRetryAfterTicks;
        if (_monitorDirty || retryMissing || !string.Equals(_monitorSelectedFor, requestedOutput, StringComparison.OrdinalIgnoreCase))
        {
            SelectMonitor(requestedOutput);
        }

        if (_monitor is null)
        {
            _window?.Hide();
            ReleaseRenderer();
            SetStatus(OverlayStatus.MonitorNotFound);
            return;
        }

        EnsureWindow(_monitor.Bounds);
        var window = _window!;

        if (!OverlayWindowPolicy.MayShow(window.Exclusion))
        {
            window.Hide();
            ReleaseRenderer();
            SetStatus(OverlayStatus.CaptureExclusionUnavailable);
            return;
        }

        if (!overlay.Enabled)
        {
            window.Hide();
            ReleaseRenderer();
            SetStatus(OverlayStatus.Disabled);
            return;
        }

        EnsureRenderer(window, _monitor.DeviceName);
        var renderer = _renderer!;

        var layout = GlowGeometry.ComputeLayout(_monitor.Bounds.Width, _monitor.Bounds.Height, overlay);
        if (_configuredLayout != layout || _configuredBlendMode != overlay.BlendMode)
        {
            renderer.Configure(layout, overlay.BlendMode);
            _configuredLayout = layout;
            _configuredBlendMode = overlay.BlendMode;
        }

        if (window.ConsumeZOrderChanged())
        {
            window.ReassertTopmost();
        }

        SetStatus(OverlayStatus.Running);
        if (!_hasFrame)
        {
            return;
        }

        var colors = _input.ReadSlot.DisplayColors;
        var zones = snapshot.Zones;

        // A frame produced under the previous zone layout: wait for one that matches.
        if (colors.Length != zones.Length)
        {
            return;
        }

        PictureMask? mask = overlay.KeepPictureClear &&
            settings.Letterbox.Enabled &&
            GlowGeometry.TryGetPictureMask(layout, _input.ReadSlot.ContentBounds, out var picture)
                ? picture
                : null;

        if (!_tracker.NeedsRedraw(colors, layout, overlay, zones, mask))
        {
            if (newFrame)
            {
                Interlocked.Increment(ref _unchangedFrames);
            }

            return;
        }

        // Light comes from the screen edges, or, in letterboxed video, from the picture's edges into the bars.
        var zoneSpan = zones.AsSpan();
        var frame = mask is { } pictureArea ? GlowFrame.Picture(pictureArea) : GlowFrame.Screen(layout);
        var innerCount = GlowGeometry.BuildSegments(
            zoneSpan,
            colors,
            layout,
            frame,
            mask is null ? layout.Margin : layout.InnerSigma,
            layout.InnerSpread,
            overlay.Brightness,
            overlay.InnerIntensity * overlay.Opacity,
            _innerSegments);
        var washCount = GlowGeometry.BuildSegments(
            zoneSpan,
            colors,
            layout,
            frame,
            mask is null ? layout.Margin : layout.WashSigma,
            layout.WashSpread,
            overlay.Brightness,
            overlay.WashIntensity * overlay.Opacity,
            _washSegments);
        var renderTime = renderer.Render(_innerSegments.AsSpan(0, innerCount), _washSegments.AsSpan(0, washCount), mask);
        _tracker.MarkDrawn(colors, layout, overlay, zones, mask);

        // Shown only after the first successful present, so the window never appears with undefined content.
        window.Show();

        Interlocked.Increment(ref _redraws);
        Interlocked.Exchange(ref _lastRenderTicks, (long)(renderTime.TotalSeconds * Stopwatch.Frequency));
    }

    private void SelectMonitor(string? requestedOutput)
    {
        var selected = MonitorSelector.Select(MonitorEnumerator.GetMonitors(), requestedOutput);
        _monitorDirty = false;
        _monitorSelectedFor = requestedOutput;

        if (selected is null)
        {
            if (!_monitorMissingLogged)
            {
                OverlayLog.MonitorNotFound(_logger, requestedOutput ?? "primary");
                _monitorMissingLogged = true;
            }

            _monitor = null;
            _monitorRetryAfterTicks = Stopwatch.GetTimestamp() + (long)(MissingMonitorRetry.TotalSeconds * Stopwatch.Frequency);
            Volatile.Write(ref _monitorName, null);
            return;
        }

        if (selected != _monitor)
        {
            OverlayLog.MonitorSelected(_logger, selected.DeviceName, selected.Bounds);
        }

        _monitorMissingLogged = false;
        _monitor = selected;
        Volatile.Write(ref _monitorName, selected.DeviceName);
    }

    private void EnsureWindow(ScreenRect bounds)
    {
        if (_window is null)
        {
            _window = OverlayWindow.Create(bounds);
            Volatile.Write(ref _exclusion, (int)_window.Exclusion);
            if (_window.Exclusion == CaptureExclusion.Excluded)
            {
                OverlayLog.CaptureExcluded(_logger);
            }
            else
            {
                OverlayLog.CaptureExclusionUnavailable(_logger, _window.Exclusion, Environment.OSVersion.Version);
            }

            _tracker.Invalidate();
            return;
        }

        if (_window.Bounds != bounds)
        {
            _window.SetBounds(bounds);
        }
    }

    private void EnsureRenderer(OverlayWindow window, string monitorDeviceName)
    {
        // The renderer's device lives on the monitor's adapter; another monitor may be on another GPU.
        if (_renderer is not null && string.Equals(_rendererMonitor, monitorDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReleaseRenderer();
        _renderer = GlowRenderer.Create(window.Handle, monitorDeviceName);
        _rendererMonitor = monitorDeviceName;
        OverlayLog.RendererCreated(_logger, _renderer.AdapterName);
    }

    private void ReleaseRenderer()
    {
        _renderer?.Dispose();
        _renderer = null;
        _rendererMonitor = null;
        _configuredLayout = null;
        _configuredBlendMode = null;
        _tracker.Invalidate();
    }

    private void ReleaseAll()
    {
        ReleaseRenderer();
        _window?.Dispose();
        _window = null;
        _monitor = null;
        _monitorDirty = true;
        Volatile.Write(ref _exclusion, -1);
    }

    private void SetStatus(OverlayStatus status) => Volatile.Write(ref _status, (int)status);
}
