using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AmbientLight.Overlay.Interop;

namespace AmbientLight.Overlay.Window;

/// <summary>Lists the attached monitors with their virtual-screen rectangles.</summary>
/// <remarks>
/// Called on the overlay thread, which is per-monitor-v2 DPI aware, so rectangles are in physical pixels
/// and match the duplicated surface the capture stage sees. Runs only on display changes, so the
/// small allocations it makes are irrelevant.
/// </remarks>
public static unsafe class MonitorEnumerator
{
    /// <summary>Returns every monitor that is part of the desktop.</summary>
    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            User32.EnumDisplayMonitors(IntPtr.Zero, null, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(IntPtr monitor, IntPtr hdc, User32.Rect* clip, IntPtr data)
    {
        var info = new User32.MonitorInfoEx { Size = (uint)sizeof(User32.MonitorInfoEx) };
        if (User32.GetMonitorInfo(monitor, &info) && GCHandle.FromIntPtr(data).Target is List<MonitorDescriptor> monitors)
        {
            var name = new string(info.Device, 0, User32.MonitorInfoEx.DeviceNameLength).TrimEnd('\0');
            var bounds = new ScreenRect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top);
            monitors.Add(new MonitorDescriptor(name, bounds, (info.Flags & User32.MONITORINFOF_PRIMARY) != 0));
        }

        // Non-zero: continue the enumeration.
        return 1;
    }
}
