using System.Collections.Frozen;
using SharpGen.Runtime;
using DxgiResult = Vortice.DXGI.ResultCode;

namespace AmbientLight.Capture.Recovery;

/// <summary>What the capture loop must rebuild after a failure.</summary>
public enum CaptureRecovery
{
    /// <summary>Not an error: no new desktop image arrived within the timeout.</summary>
    None = 0,

    /// <summary>
    /// The duplication interface is invalid (DXGI_ERROR_ACCESS_LOST): display mode or HDR toggle,
    /// resolution or rotation change, fullscreen-exclusive transition. Recreate only the duplication;
    /// the D3D device is still good.
    /// </summary>
    RecreateDuplication = 1,

    /// <summary>
    /// The D3D device is gone (driver update/crash, TDR, GPU removed) or the output disappeared
    /// (monitor unplugged). Tear everything down, re-enumerate outputs and start over.
    /// </summary>
    RecreateDevice = 2,

    /// <summary>
    /// Duplication is temporarily impossible: the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del)
    /// is active, the session is disconnected, or too many applications are duplicating. Wait with
    /// back-off and recreate the duplication when it becomes available again.
    /// </summary>
    WaitThenRecreateDuplication = 3,

    /// <summary>
    /// An error that retrying immediately will not fix (unsupported configuration, programming error).
    /// The loop keeps retrying at the maximum back-off interval so a later configuration change
    /// (driver install, monitor reconnect) is still picked up without restarting the app.
    /// </summary>
    Fatal = 4,

    /// <summary>
    /// Desktop Duplication is unsupported from this adapter: on a hybrid laptop the device was created on the
    /// discrete GPU while the panel is driven by the integrated one. Exclude this adapter and rebuild the
    /// device on another GPU that exposes the same monitor.
    /// </summary>
    SwitchAdapter = 5,
}

/// <summary>Maps HRESULTs from DXGI / D3D11 to a <see cref="CaptureRecovery"/> action.</summary>
public static class DxgiErrorClassifier
{
    private static readonly FrozenDictionary<int, CaptureRecovery> Map = new Dictionary<int, CaptureRecovery>
    {
        [DxgiResult.WaitTimeout.Code] = CaptureRecovery.None,

        [DxgiResult.AccessLost.Code] = CaptureRecovery.RecreateDuplication,
        [DxgiResult.ModeChangeInProgress.Code] = CaptureRecovery.WaitThenRecreateDuplication,

        [DxgiResult.DeviceRemoved.Code] = CaptureRecovery.RecreateDevice,
        [DxgiResult.DeviceReset.Code] = CaptureRecovery.RecreateDevice,
        [DxgiResult.DeviceHung.Code] = CaptureRecovery.RecreateDevice,
        [DxgiResult.DriverInternalError.Code] = CaptureRecovery.RecreateDevice,
        [DxgiResult.NotFound.Code] = CaptureRecovery.RecreateDevice,
        [Result.OutOfMemory.Code] = CaptureRecovery.RecreateDevice,

        // E_ACCESSDENIED is what DuplicateOutput returns while the secure desktop is shown.
        [Result.AccessDenied.Code] = CaptureRecovery.WaitThenRecreateDuplication,
        [DxgiResult.AccessDenied.Code] = CaptureRecovery.WaitThenRecreateDuplication,
        [DxgiResult.NotCurrentlyAvailable.Code] = CaptureRecovery.WaitThenRecreateDuplication,
        [DxgiResult.SessionDisconnected.Code] = CaptureRecovery.WaitThenRecreateDuplication,
        [DxgiResult.RemoteClientDisconnected.Code] = CaptureRecovery.WaitThenRecreateDuplication,

        [DxgiResult.Unsupported.Code] = CaptureRecovery.Fatal,
        [DxgiResult.InvalidCall.Code] = CaptureRecovery.Fatal,
        [Result.InvalidArg.Code] = CaptureRecovery.Fatal,
    }.ToFrozenDictionary();

    /// <summary>Classifies an HRESULT. Unknown failures are treated as device loss, the most thorough reset.</summary>
    public static CaptureRecovery Classify(int hresult)
    {
        if (Map.TryGetValue(hresult, out var recovery))
        {
            return recovery;
        }

        return hresult >= 0 ? CaptureRecovery.None : CaptureRecovery.RecreateDevice;
    }

    /// <summary>Classifies an exception thrown by the capture stack.</summary>
    public static CaptureRecovery Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            CaptureException capture => capture.Recovery,
            SharpGenException sharpGen => Classify(sharpGen.ResultCode.Code),
            _ => CaptureRecovery.Fatal,
        };
    }
}

/// <summary>A capture failure that carries its own recovery decision.</summary>
public sealed class CaptureException : Exception
{
    /// <summary>Creates a fatal capture exception with a generic message.</summary>
    public CaptureException()
        : this("Capture failed.", CaptureRecovery.Fatal)
    {
    }

    /// <summary>Creates a fatal capture exception.</summary>
    public CaptureException(string message)
        : this(message, CaptureRecovery.Fatal)
    {
    }

    /// <summary>Wraps <paramref name="innerException"/>, deriving the recovery from its HRESULT.</summary>
    public CaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
        Recovery = DxgiErrorClassifier.Classify(innerException);
    }

    /// <summary>Creates a capture exception with an explicit recovery.</summary>
    public CaptureException(string message, CaptureRecovery recovery)
        : base(message)
    {
        Recovery = recovery;
    }

    /// <summary>What the capture loop must rebuild.</summary>
    public CaptureRecovery Recovery { get; }
}
