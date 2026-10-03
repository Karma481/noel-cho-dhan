using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AmbientLight.Capture.Interop;

/// <summary>Kernel32 / Avrt entry points used to give the capture thread precise, low-jitter scheduling.</summary>
internal static partial class ThreadingInterop
{
    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+): sub-millisecond timer without timeBeginPeriod.</summary>
    public const uint CreateWaitableTimerHighResolution = 0x00000002;

    /// <summary>TIMER_ALL_ACCESS.</summary>
    public const uint TimerAllAccess = 0x001F0003;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeWaitHandle CreateWaitableTimerEx(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(
        SafeWaitHandle timer,
        in long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    /// <summary>
    /// Sets the DPI awareness of the calling thread only. <c>IDXGIOutput5::DuplicateOutput1</c> requires
    /// per-monitor-v2 awareness; setting it on the capture thread makes HDR capture independent of the
    /// host application's manifest. Returns the previous context, or zero on failure.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
}
