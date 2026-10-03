using Vortice.DXGI;

namespace AmbientLight.Capture.Duplication;

/// <summary>Result of one <c>AcquireNextFrame</c> call.</summary>
public enum AcquireOutcome
{
    /// <summary>No desktop update arrived within the timeout (static screen). Not an error.</summary>
    Timeout = 0,

    /// <summary>A frame is held and must be processed or skipped before the next acquire.</summary>
    Acquired = 1,
}

/// <summary>Metadata of the frame currently held by <see cref="DesktopDuplicator"/>.</summary>
/// <param name="LastPresentTime">QPC time of the last present folded into this frame; 0 for pointer-only updates.</param>
/// <param name="AccumulatedFrames">Presents accumulated since the previous acquire (more than 1 means frames were coalesced).</param>
/// <param name="ProtectedContentMaskedOut">DRM content was blacked out in this image.</param>
public readonly record struct AcquiredFrame(long LastPresentTime, uint AccumulatedFrames, bool ProtectedContentMaskedOut)
{
    /// <summary>
    /// False when only the mouse pointer changed: the desktop image is identical to the previous one,
    /// so the GPU reduction can be skipped entirely.
    /// </summary>
    public bool HasNewImage => LastPresentTime != 0;

    internal static AcquiredFrame From(in OutduplFrameInfo info) =>
        new(info.LastPresentTime, info.AccumulatedFrames, info.ProtectedContentMaskedOut);
}
