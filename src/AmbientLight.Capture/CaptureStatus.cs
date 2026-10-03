using AmbientLight.Capture.ColorSpace;

namespace AmbientLight.Capture;

/// <summary>Lifecycle state of <see cref="DesktopCaptureService"/>, for the UI and diagnostics.</summary>
public enum CaptureStatus
{
    /// <summary>The capture thread is not running.</summary>
    Stopped = 0,

    /// <summary>Creating the device and the duplication.</summary>
    Starting = 1,

    /// <summary>Frames are being captured (or the screen is static and timeouts are expected).</summary>
    Capturing = 2,

    /// <summary>Rebuilding after access lost or device removal.</summary>
    Recovering = 3,

    /// <summary>
    /// The secure desktop (UAC, lock screen), a disconnected session or another exclusive user prevents
    /// duplication; capture resumes automatically when it becomes available.
    /// </summary>
    WaitingForDesktop = 4,

    /// <summary>A non-transient error occurred; retried every few seconds.</summary>
    Faulted = 5,
}

/// <summary>Point-in-time counters of the capture stage. All counts are since <see cref="DesktopCaptureService.Start"/>.</summary>
/// <param name="FramesAcquired">Successful <c>AcquireNextFrame</c> calls.</param>
/// <param name="FramesPublished">Frames reduced on the GPU and handed to the processing stage.</param>
/// <param name="PointerOnlyFrames">Acquired frames skipped because only the mouse pointer moved.</param>
/// <param name="Timeouts">Acquire timeouts (static screen).</param>
/// <param name="DuplicationRecreations">Duplication interfaces recreated after access lost or secure-desktop waits.</param>
/// <param name="DeviceRecreations">Full device rebuilds after device removal or output changes.</param>
/// <param name="LastGpuReduceTime">Submission-to-readback time of the most recent reduction.</param>
/// <param name="ProtectedContentMasked">The latest frame had DRM-protected content blacked out by the OS.</param>
/// <param name="UsesLegacyDuplication">The SDR-only <c>DuplicateOutput</c> fallback is active.</param>
/// <param name="Encoding">Pixel encoding of the latest captured surface.</param>
public readonly record struct CaptureStatistics(
    long FramesAcquired,
    long FramesPublished,
    long PointerOnlyFrames,
    long Timeouts,
    long DuplicationRecreations,
    long DeviceRecreations,
    TimeSpan LastGpuReduceTime,
    bool ProtectedContentMasked,
    bool UsesLegacyDuplication,
    SurfaceEncoding Encoding);
