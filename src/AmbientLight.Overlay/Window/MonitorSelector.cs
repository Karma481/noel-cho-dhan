namespace AmbientLight.Overlay.Window;

/// <summary>A rectangle in virtual-screen coordinates (physical pixels, per-monitor-v2 DPI aware).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    /// <summary>True when the rectangle has a positive area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>One attached monitor as reported by <c>GetMonitorInfo</c>.</summary>
/// <param name="DeviceName">GDI device name, for example <c>\\.\DISPLAY2</c>; the same name DXGI reports.</param>
/// <param name="Bounds">The monitor's rectangle in the virtual screen. Secondary monitors may have negative coordinates.</param>
/// <param name="IsPrimary">True for the primary monitor (whose top-left corner is the virtual-screen origin).</param>
public sealed record MonitorDescriptor(string DeviceName, ScreenRect Bounds, bool IsPrimary);

/// <summary>Picks the monitor the overlay must cover: the one the capture stage is capturing.</summary>
public static class MonitorSelector
{
    /// <summary>
    /// Returns the monitor named <paramref name="outputDeviceName"/> (case-insensitive), or the primary
    /// monitor when it is <see langword="null"/>, mirroring <c>CaptureSettings.OutputDeviceName</c>.
    /// Returns <see langword="null"/> when no such monitor is attached.
    /// </summary>
    public static MonitorDescriptor? Select(IReadOnlyList<MonitorDescriptor> monitors, string? outputDeviceName)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (outputDeviceName is not null)
        {
            return monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceName, outputDeviceName, StringComparison.OrdinalIgnoreCase));
        }

        // The capture stage identifies the primary output by its desktop origin; accept either signal.
        return monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors.FirstOrDefault(monitor => monitor.Bounds.Left == 0 && monitor.Bounds.Top == 0);
    }
}
