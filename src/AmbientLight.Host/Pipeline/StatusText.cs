using System.Globalization;
using AmbientLight.Capture;
using AmbientLight.Overlay;
using AmbientLight.Overlay.Window;
using AmbientLight.Serial;

namespace AmbientLight.Host.Pipeline;

/// <summary>Frames per second from a monotonically increasing frame counter sampled at UI rate.</summary>
public sealed class RateMeter
{
    private long _lastCount = -1;
    private TimeSpan _lastTime;

    /// <summary>The rate between the previous sample and this one (0 for the first sample or a counter reset).</summary>
    public double Sample(long count, TimeSpan timestamp)
    {
        var previousCount = _lastCount;
        var previousTime = _lastTime;
        _lastCount = count;
        _lastTime = timestamp;

        var elapsed = (timestamp - previousTime).TotalSeconds;
        if (previousCount < 0 || count < previousCount || elapsed <= 0)
        {
            return 0;
        }

        return (count - previousCount) / elapsed;
    }

    /// <summary>Forgets the previous sample.</summary>
    public void Reset() => _lastCount = -1;
}

/// <summary>Human-readable descriptions of the pipeline for the tray tooltip and the Settings window.</summary>
public static class StatusText
{
    /// <summary>One line summarizing the orchestrator state (kept short: the tray tooltip holds 127 characters).</summary>
    public static string Describe(PipelineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var text = state.Plan.Mode switch
        {
            PipelineMode.Running when state.Plan.OverlaySuspended => "LEDs only (overlay paused for fullscreen game)",
            PipelineMode.Running => "Running",
            PipelineMode.Paused => "Paused",
            PipelineMode.SuspendedForFullscreen => "Overlay paused for a fullscreen game",
            PipelineMode.Idle => "Idle: overlay and LED strip are off",
            PipelineMode.Stopped => "Stopped",
            _ => state.Plan.Mode.ToString(),
        };

        return state.LastError is null ? text : text + " (error, see Settings)";
    }

    /// <summary>The capture line of the status footer.</summary>
    /// <param name="status">Stage snapshot.</param>
    /// <param name="framesPerSecond">Published capture frames per second over the last sampling period.</param>
    public static string DescribeCapture(PipelineStatus status, double framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(status);
        var output = status.CapturedOutput;
        var where = output is null ? string.Empty : $" · {ShortMonitorName(output.DeviceName)} on {output.AdapterName}";
        return status.CaptureStatus switch
        {
            CaptureStatus.Stopped => "Capture: stopped",
            CaptureStatus.Starting => "Capture: starting…",
            CaptureStatus.Capturing => "Capture: "
                + (framesPerSecond >= 0.5
                    ? string.Create(CultureInfo.InvariantCulture, $"{framesPerSecond:0} fps")
                    : "idle (static screen)")
                + where
                + (output?.IsHdr == true ? " · HDR" : string.Empty)
                + (status.Capture.ProtectedContentMasked ? " · protected video is black to capture" : string.Empty),
            CaptureStatus.Recovering => "Capture: recovering after a display change…",
            CaptureStatus.WaitingForDesktop => "Capture: waiting for the desktop (lock screen or UAC prompt)…",
            CaptureStatus.Faulted => "Capture: error, retrying (see the log)" + where,
            _ => "Capture: " + status.CaptureStatus,
        };
    }

    /// <summary>The overlay line of the status footer, including whether the glow is hidden from screen capture.</summary>
    public static string DescribeOverlay(PipelineStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var overlay = status.Overlay;
        return status.OverlayStatus switch
        {
            OverlayStatus.Stopped or OverlayStatus.Disabled => "Overlay: off",
            OverlayStatus.Running => "Overlay: on"
                + (overlay.MonitorDeviceName is null ? string.Empty : " · " + ShortMonitorName(overlay.MonitorDeviceName))
                + (overlay.Exclusion == CaptureExclusion.Excluded ? " · hidden from screen capture ✓" : string.Empty),
            OverlayStatus.CaptureExclusionUnavailable => overlay.Exclusion == CaptureExclusion.UnsupportedOperatingSystem
                ? "Overlay: hidden (needs Windows 10 2004+ to stay out of screenshots)"
                : "Overlay: hidden (Windows refused to exclude it from screen capture)",
            OverlayStatus.MonitorNotFound => "Overlay: the selected monitor is not connected",
            OverlayStatus.Recovering => "Overlay: recovering the graphics device…",
            OverlayStatus.Faulted => "Overlay: error, retrying (see the log)",
            _ => "Overlay: " + status.OverlayStatus,
        };
    }

    /// <summary>The LED strip line of the status footer.</summary>
    /// <param name="status">Stage snapshot.</param>
    /// <param name="framesPerSecond">Frames sent per second over the last sampling period.</param>
    public static string DescribeSerial(PipelineStatus status, double framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Serial is not { } serial)
        {
            return "LED strip: off";
        }

        var port = string.IsNullOrEmpty(serial.PortName) ? "the port" : serial.PortName;
        return serial.Status switch
        {
            SerialOutputStatus.Stopped or SerialOutputStatus.Disabled => "LED strip: off",
            SerialOutputStatus.Connecting => $"LED strip: connecting to {port}…",
            SerialOutputStatus.Connected => string.Create(CultureInfo.InvariantCulture, $"LED strip: connected · {port} · {framesPerSecond:0} fps"),
            SerialOutputStatus.PortNotFound => $"LED strip: {port} not found, plug in the controller",
            SerialOutputStatus.PortBusy => $"LED strip: {port} is in use by another app",
            SerialOutputStatus.Reconnecting => $"LED strip: {port} dropped out, reconnecting…",
            SerialOutputStatus.InvalidConfiguration => $"LED strip: the {port} driver rejected the baud rate",
            _ => "LED strip: " + serial.Status,
        };
    }

    /// <summary><c>\\.\DISPLAY1</c> → <c>DISPLAY1</c>.</summary>
    internal static string ShortMonitorName(string deviceName) =>
        deviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? deviceName[4..] : deviceName;
}
