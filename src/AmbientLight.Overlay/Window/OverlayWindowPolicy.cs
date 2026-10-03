using AmbientLight.Overlay.Interop;

namespace AmbientLight.Overlay.Window;

/// <summary>Outcome of excluding the overlay from screen capture.</summary>
public enum CaptureExclusion
{
    /// <summary>WDA_EXCLUDEFROMCAPTURE is in effect: capture never sees the glow.</summary>
    Excluded = 0,

    /// <summary>The OS predates Windows 10 version 2004 (build 19041), which introduced the flag.</summary>
    UnsupportedOperatingSystem = 1,

    /// <summary>The call failed or the affinity did not stick (read back as something else).</summary>
    Failed = 2,
}

/// <summary>
/// Window styles and the capture-exclusion rule of the overlay, kept free of Win32 calls so they are
/// unit-testable.
/// </summary>
public static class OverlayWindowPolicy
{
    /// <summary>First Windows build supporting <c>WDA_EXCLUDEFROMCAPTURE</c> (Windows 10 version 2004).</summary>
    public static readonly Version MinimumExclusionVersion = new(10, 0, 19041);

    /// <summary>A borderless popup: no caption, no frame, no system menu.</summary>
    public const uint Style = User32.WS_POPUP;

    /// <summary>
    /// <list type="bullet">
    /// <item><c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>: the window is invisible to hit testing, so every click
    /// and scroll goes to the window underneath.</item>
    /// <item><c>WS_EX_NOACTIVATE</c>: never becomes the foreground window, never takes keyboard focus, even
    /// when clicked or shown.</item>
    /// <item><c>WS_EX_TOOLWINDOW</c>: no taskbar button and absent from Alt+Tab / Win+Tab.</item>
    /// <item><c>WS_EX_TOPMOST</c>: stays above normal windows (re-asserted when another app comes to the front).</item>
    /// <item><c>WS_EX_NOREDIRECTIONBITMAP</c>: no GDI redirection surface; the only content is the
    /// DirectComposition visual, so there is no full-screen system-memory bitmap to allocate or compose.</item>
    /// </list>
    /// </summary>
    public const uint ExtendedStyle =
        User32.WS_EX_LAYERED |
        User32.WS_EX_TRANSPARENT |
        User32.WS_EX_TOOLWINDOW |
        User32.WS_EX_NOACTIVATE |
        User32.WS_EX_TOPMOST |
        User32.WS_EX_NOREDIRECTIONBITMAP;

    /// <summary>Flags for every SetWindowPos call: never activate, never drag owned windows along.</summary>
    public const uint PositionFlags = User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER;

    /// <summary>True when <paramref name="osVersion"/> supports excluding a window from capture.</summary>
    public static bool SupportsCaptureExclusion(Version osVersion)
    {
        ArgumentNullException.ThrowIfNull(osVersion);
        return osVersion >= MinimumExclusionVersion;
    }

    /// <summary>
    /// Whether the overlay may be shown. Showing it without exclusion would let the capture stage sample the
    /// glow and feed it back into the LEDs and the glow itself, a loop that drifts towards saturated colors.
    /// <c>WDA_MONITOR</c> is not a usable fallback either: it blacks the window out in captures, and this
    /// window covers the whole monitor, so every capture would be black. The only safe fallback is to keep
    /// the overlay hidden; the LED output is unaffected.
    /// </summary>
    public static bool MayShow(CaptureExclusion exclusion) => exclusion == CaptureExclusion.Excluded;
}
