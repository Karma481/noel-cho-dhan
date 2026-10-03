using AmbientLight.Overlay.Window;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Overlay;

/// <summary>Source-generated, allocation-free log messages for the overlay stage.</summary>
internal static partial class OverlayLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Overlay started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Overlay stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information,
        Message = "Overlay covers {DeviceName} at {Bounds}")]
    public static partial void MonitorSelected(ILogger logger, string deviceName, ScreenRect bounds);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Warning,
        Message = "Overlay monitor {DeviceName} is not attached; the overlay stays hidden until it is")]
    public static partial void MonitorNotFound(ILogger logger, string deviceName);

    [LoggerMessage(EventId = 3020, Level = LogLevel.Information,
        Message = "Overlay window is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE)")]
    public static partial void CaptureExcluded(ILogger logger);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Warning,
        Message = "Overlay cannot be excluded from screen capture ({Reason}, Windows {OsVersion}); it stays hidden to prevent a capture feedback loop. LED output is unaffected.")]
    public static partial void CaptureExclusionUnavailable(ILogger logger, CaptureExclusion reason, Version osVersion);

    [LoggerMessage(EventId = 3030, Level = LogLevel.Information, Message = "Overlay renders on {AdapterName}")]
    public static partial void RendererCreated(ILogger logger, string adapterName);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Warning, Message = "Overlay rendering device lost; recreating it")]
    public static partial void DeviceLost(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3040, Level = LogLevel.Error, Message = "Overlay failed; retrying in {DelayMs} ms")]
    public static partial void Faulted(ILogger logger, Exception exception, double delayMs);

    [LoggerMessage(EventId = 3041, Level = LogLevel.Warning,
        Message = "Per-monitor-v2 DPI awareness could not be set on the overlay thread; the overlay may be mis-sized on scaled displays")]
    public static partial void DpiAwarenessUnavailable(ILogger logger);
}
