namespace AmbientLight.Capture.Recovery;

/// <summary>
/// Exponential back-off between recovery attempts: immediate first retry, then doubling from
/// <see cref="InitialDelay"/> up to <see cref="MaximumDelay"/>. Not thread-safe; owned by the capture thread.
/// </summary>
public sealed class RecoveryBackoff
{
    /// <summary>Delay before the second consecutive attempt.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Upper bound; also the retry period for <see cref="CaptureRecovery.Fatal"/> errors.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);

    private int _consecutiveFailures;

    /// <summary>Number of failures since the last success.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Records a failure and returns how long to wait before the next attempt.</summary>
    public TimeSpan NextDelay(CaptureRecovery recovery)
    {
        _consecutiveFailures++;

        if (recovery == CaptureRecovery.Fatal)
        {
            return MaximumDelay;
        }

        // Access lost is routine (every mode change) and usually recoverable at once.
        if (_consecutiveFailures == 1 && recovery == CaptureRecovery.RecreateDuplication)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Min(_consecutiveFailures - 1, 16);
        var delay = InitialDelay * Math.Pow(2, exponent);
        return delay < MaximumDelay ? delay : MaximumDelay;
    }

    /// <summary>Clears the failure count after a successful frame.</summary>
    public void Reset() => _consecutiveFailures = 0;
}
