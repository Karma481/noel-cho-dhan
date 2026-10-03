using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AmbientLight.Capture.Interop;
using Microsoft.Win32.SafeHandles;

namespace AmbientLight.Capture.Threading;

/// <summary>
/// Sleeps until a <see cref="Stopwatch"/> deadline with sub-millisecond precision.
/// </summary>
/// <remarks>
/// <see cref="Thread.Sleep(int)"/> rounds up to the system timer tick (15.6 ms by default), which alone
/// would blow the frame budget. A high-resolution waitable timer (Windows 10 1803+) wakes within
/// ~0.5 ms without raising the global timer resolution. On older systems it falls back to a normal
/// waitable timer, which is still better than Sleep.
/// </remarks>
internal sealed class PrecisionTimer : IDisposable
{
    private readonly TimerWaitHandle _timer;

    public PrecisionTimer()
    {
        var handle = ThreadingInterop.CreateWaitableTimerEx(
            IntPtr.Zero,
            timerName: null,
            ThreadingInterop.CreateWaitableTimerHighResolution,
            ThreadingInterop.TimerAllAccess);

        IsHighResolution = !handle.IsInvalid;
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = ThreadingInterop.CreateWaitableTimerEx(IntPtr.Zero, timerName: null, flags: 0, ThreadingInterop.TimerAllAccess);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "CreateWaitableTimerEx failed.");
            }
        }

        _timer = new TimerWaitHandle(handle);
    }

    /// <summary>True when the high-resolution timer flag was accepted by the OS.</summary>
    public bool IsHighResolution { get; }

    /// <summary>Blocks the calling thread until <paramref name="deadlineTicks"/> (Stopwatch ticks) has passed.</summary>
    public void WaitUntil(long deadlineTicks)
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadlineTicks);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        // Negative due time = relative interval in 100 ns units.
        var dueTime = -remaining.Ticks;
        if (!ThreadingInterop.SetWaitableTimer(_timer.SafeWaitHandle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, resume: false))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed.");
        }

        _timer.WaitOne();
    }

    public void Dispose() => _timer.Dispose();

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}
