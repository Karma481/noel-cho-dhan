using AmbientLight.Capture.Recovery;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Capture;

/// <summary>Source-generated, allocation-free log messages for the capture stage.</summary>
internal static partial class CaptureLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Capturing {DeviceName} ({Width}x{Height}, rotation {Rotation}, HDR {IsHdr}, SDR white {SdrWhiteNits} nits, peak {PeakNits} nits) on {AdapterName}")]
    public static partial void CaptureStarted(
        ILogger logger, string deviceName, int width, int height, Vortice.DXGI.ModeRotation rotation, bool isHdr, float sdrWhiteNits, float peakNits, string adapterName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "DuplicateOutput1 is unavailable; using the legacy SDR-only duplication API")]
    public static partial void LegacyDuplication(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "GPU completion uses {Mechanism}")]
    public static partial void GpuSynchronization(ILogger logger, string mechanism);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Capture interrupted ({Recovery}): {Reason}. Retrying in {DelayMs} ms (attempt {Attempt})")]
    public static partial void Recovering(ILogger logger, CaptureRecovery recovery, string reason, double delayMs, int attempt);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Debug,
        Message = "Capture still unavailable ({Recovery}): {Reason}. Retrying in {DelayMs} ms (attempt {Attempt})")]
    public static partial void StillRecovering(ILogger logger, CaptureRecovery recovery, string reason, double delayMs, int attempt);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Error,
        Message = "Capture failed ({Recovery}); retrying in {DelayMs} ms")]
    public static partial void Faulted(ILogger logger, Exception exception, CaptureRecovery recovery, double delayMs);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning,
        Message = "MMCSS registration failed; the capture thread runs at AboveNormal priority only")]
    public static partial void MmcssUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Warning,
        Message = "Per-monitor-v2 DPI awareness could not be set on the capture thread; HDR capture may fall back to SDR")]
    public static partial void DpiAwarenessUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Capture stopped")]
    public static partial void Stopped(ILogger logger);
}
