using AmbientLight.Capture.Interop;

namespace AmbientLight.Capture.Threading;

/// <summary>
/// Registers the calling thread with the Multimedia Class Scheduler Service so it is scheduled ahead of
/// ordinary threads (games, browsers) and is not starved while the CPU is busy.
/// Must be created and disposed on the same thread.
/// </summary>
internal sealed class MmcssRegistration : IDisposable
{
    private IntPtr _handle;

    private MmcssRegistration(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Registers the current thread under <paramref name="taskName"/> (a key under
    /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks</c>).
    /// Returns <see langword="null"/> when MMCSS is unavailable (service disabled); the caller then
    /// keeps the thread priority it already set.
    /// </summary>
    public static MmcssRegistration? TryRegisterCurrentThread(string taskName)
    {
        uint taskIndex = 0;
        var handle = ThreadingInterop.AvSetMmThreadCharacteristics(taskName, ref taskIndex);
        return handle == IntPtr.Zero ? null : new MmcssRegistration(handle);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            ThreadingInterop.AvRevertMmThreadCharacteristics(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
