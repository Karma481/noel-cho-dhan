using System.Runtime.InteropServices;

namespace AmbientLight.Host.Platform;

/// <summary>Asks the shell whether a Direct3D application runs in exclusive fullscreen.</summary>
public static partial class FullscreenDetector
{
    /// <summary><c>QUNS_RUNNING_D3D_FULL_SCREEN</c>: a Direct3D application is in exclusive fullscreen mode.</summary>
    public const int RunningD3DFullScreen = 3;

    /// <summary>
    /// True for the <c>QUERY_USER_NOTIFICATION_STATE</c> values that mean exclusive fullscreen. Borderless
    /// fullscreen games and fullscreen video report <c>QUNS_BUSY</c> instead; DWM still composes those, so the
    /// glow shows over them and the overlay is kept.
    /// </summary>
    public static bool IsExclusiveFullscreen(int notificationState) => notificationState == RunningD3DFullScreen;

    /// <summary>Queries the shell. Returns <see langword="false"/> when the query fails.</summary>
    public static bool IsExclusiveFullscreenActive() =>
        SHQueryUserNotificationState(out var state) >= 0 && IsExclusiveFullscreen(state);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// Polls <see cref="FullscreenDetector"/> once per interval and reports transitions. A transition is reported
/// only after it has been seen on <see cref="ConfirmationPolls"/> consecutive polls, so alt-tabbing through a
/// game does not tear the overlay down and rebuild it each time.
/// </summary>
/// <remarks>
/// One shell call per second is the whole cost; the monitor is disabled while the "pause in exclusive
/// fullscreen" option is off.
/// </remarks>
public sealed class FullscreenMonitor : IDisposable
{
    /// <summary>Default polling period.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive identical readings needed to report a change.</summary>
    public const int ConfirmationPolls = 2;

    private readonly Func<bool> _probe;
    private readonly TimeSpan _interval;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();

    private bool _enabled;
    private bool _active;
    private bool _candidate;
    private int _candidateCount;
    private int _polling;
    private bool _disposed;

    /// <summary>Creates a stopped monitor.</summary>
    /// <param name="probe">Returns whether exclusive fullscreen is active; <see cref="FullscreenDetector.IsExclusiveFullscreenActive"/> in the app.</param>
    /// <param name="interval">Polling period; <see cref="DefaultInterval"/> when <see langword="null"/>.</param>
    /// <param name="timeProvider">Timer source; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    public FullscreenMonitor(Func<bool> probe, TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _interval = interval ?? DefaultInterval;
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(
            static state => ((FullscreenMonitor)state!).Poll(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Raised on a timer thread (or the thread calling <see cref="SetEnabled"/>) when the confirmed state
    /// changes. The argument is the new state.
    /// </summary>
    public event EventHandler<bool>? Changed;

    /// <summary>The confirmed state; always <see langword="false"/> while disabled.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>Starts or stops polling. Disabling reports a transition back to inactive if needed.</summary>
    public void SetEnabled(bool enabled)
    {
        var raiseInactive = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            _candidateCount = 0;
            if (!enabled && _active)
            {
                _active = false;
                raiseInactive = true;
            }

            _timer.Change(enabled ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, enabled ? _interval : Timeout.InfiniteTimeSpan);
        }

        if (raiseInactive)
        {
            Changed?.Invoke(this, false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
        }

        _timer.Dispose();
    }

    private void Poll()
    {
        // A slow shell call must not let two polls overlap and report transitions out of order.
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var reading = _probe();
            bool raise;
            lock (_lock)
            {
                raise = Observe(reading);
            }

            if (raise)
            {
                Changed?.Invoke(this, reading);
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private bool Observe(bool reading)
    {
        if (!_enabled)
        {
            return false;
        }

        if (reading == _active)
        {
            _candidateCount = 0;
            return false;
        }

        if (_candidateCount == 0 || _candidate != reading)
        {
            _candidate = reading;
            _candidateCount = 1;
        }
        else
        {
            _candidateCount++;
        }

        if (_candidateCount < ConfirmationPolls)
        {
            return false;
        }

        _active = reading;
        _candidateCount = 0;
        return true;
    }
}
