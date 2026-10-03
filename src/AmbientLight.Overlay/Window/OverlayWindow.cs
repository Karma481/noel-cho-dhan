using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AmbientLight.Overlay.Interop;

namespace AmbientLight.Overlay.Window;

/// <summary>
/// The overlay's top-level Win32 window: borderless, click-through, never activated, excluded from capture.
/// Must be created, used and disposed on one thread, which also pumps its messages.
/// </summary>
/// <remarks>
/// <para>Focus is never taken, by construction:</para>
/// <list type="bullet">
/// <item><c>WS_EX_NOACTIVATE</c>, and every <c>SetWindowPos</c> uses <c>SWP_NOACTIVATE</c>;</item>
/// <item><c>WM_MOUSEACTIVATE</c> answers <c>MA_NOACTIVATE</c> and <c>WM_NCHITTEST</c> answers
/// <c>HTTRANSPARENT</c>, in case a message ever reaches the window despite <c>WS_EX_TRANSPARENT</c>;</item>
/// <item>the window is shown with <c>SWP_SHOWWINDOW</c>, never with an activating <c>ShowWindow</c> command,
/// and <c>SetForegroundWindow</c> is never called.</item>
/// </list>
/// <para>
/// Topmost is re-asserted whenever another application comes to the foreground (a WinEvent hook on
/// <c>EVENT_SYSTEM_FOREGROUND</c>), instead of on a timer, so the overlay never fights other topmost
/// windows in a loop.
/// </para>
/// </remarks>
internal sealed unsafe class OverlayWindow : IDisposable
{
    private const string ClassName = "AmbientLight.Overlay";
    private const string WindowTitle = "AmbientLight Overlay";
    private const int ErrorClassAlreadyExists = 1410;

    [ThreadStatic]
    private static OverlayWindow? t_current;

    private readonly IntPtr _instance;
    private IntPtr _hwnd;
    private IntPtr _foregroundHook;
    private ScreenRect _bounds;
    private bool _visible;
    private bool _geometryChanged;
    private bool _zOrderChanged;

    private OverlayWindow(IntPtr instance, IntPtr hwnd, ScreenRect bounds)
    {
        _instance = instance;
        _hwnd = hwnd;
        _bounds = bounds;
    }

    /// <summary>The window handle.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>Whether the overlay is excluded from screen capture (it may only be shown if so).</summary>
    public CaptureExclusion Exclusion { get; private set; }

    /// <summary>Current position in virtual-screen coordinates.</summary>
    public ScreenRect Bounds => _bounds;

    /// <summary>True while the window is shown.</summary>
    public bool IsVisible => _visible;

    /// <summary>
    /// Creates the (hidden) window over <paramref name="bounds"/> on the calling thread, which must already be
    /// per-monitor-v2 DPI aware so that coordinates are physical pixels.
    /// </summary>
    /// <exception cref="Win32Exception">The window class or window could not be created.</exception>
    public static OverlayWindow Create(ScreenRect bounds)
    {
        if (t_current is not null)
        {
            throw new InvalidOperationException("This thread already owns an overlay window.");
        }

        var instance = User32.GetModuleHandle(null);
        RegisterWindowClass(instance);

        IntPtr hwnd;
        fixed (char* className = ClassName)
        fixed (char* title = WindowTitle)
        {
            hwnd = User32.CreateWindowEx(
                OverlayWindowPolicy.ExtendedStyle,
                className,
                title,
                OverlayWindowPolicy.Style,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
        }

        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed for the overlay window.");
        }

        var window = new OverlayWindow(instance, hwnd, bounds);
        t_current = window;
        try
        {
            // A layered window is not drawn until its layering attributes are set. Constant alpha 255 leaves
            // per-pixel transparency entirely to the premultiplied DirectComposition content.
            if (!User32.SetLayeredWindowAttributes(hwnd, 0, byte.MaxValue, User32.LWA_ALPHA))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetLayeredWindowAttributes failed.");
            }

            window.Exclusion = ApplyCaptureExclusion(hwnd, Environment.OSVersion.Version);
            window._foregroundHook = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND,
                User32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                &OnForegroundChanged,
                0,
                0,
                User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Excludes the window from every capture path (Desktop Duplication, Windows.Graphics.Capture, BitBlt,
    /// PrintWindow) and verifies that the affinity actually stuck.
    /// </summary>
    internal static CaptureExclusion ApplyCaptureExclusion(IntPtr hwnd, Version osVersion)
    {
        if (!OverlayWindowPolicy.SupportsCaptureExclusion(osVersion))
        {
            return CaptureExclusion.UnsupportedOperatingSystem;
        }

        if (!User32.SetWindowDisplayAffinity(hwnd, User32.WDA_EXCLUDEFROMCAPTURE))
        {
            return CaptureExclusion.Failed;
        }

        return User32.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == User32.WDA_EXCLUDEFROMCAPTURE
            ? CaptureExclusion.Excluded
            : CaptureExclusion.Failed;
    }

    /// <summary>Moves and resizes the window over <paramref name="bounds"/> (keeps it topmost, never activates it).</summary>
    public void SetBounds(ScreenRect bounds)
    {
        _bounds = bounds;
        var flags = OverlayWindowPolicy.PositionFlags | (_visible ? User32.SWP_SHOWWINDOW : 0);
        if (!User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowPos failed while positioning the overlay.");
        }
    }

    /// <summary>Shows the window without activating it. Refused unless the window is excluded from capture.</summary>
    public void Show()
    {
        if (_visible)
        {
            return;
        }

        if (!OverlayWindowPolicy.MayShow(Exclusion))
        {
            throw new InvalidOperationException("The overlay must not be shown while it is visible to screen capture.");
        }

        if (!User32.SetWindowPos(
                _hwnd,
                User32.HWND_TOPMOST,
                _bounds.Left,
                _bounds.Top,
                _bounds.Width,
                _bounds.Height,
                OverlayWindowPolicy.PositionFlags | User32.SWP_SHOWWINDOW))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowPos failed while showing the overlay.");
        }

        _visible = true;
    }

    /// <summary>Hides the window.</summary>
    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        User32.ShowWindow(_hwnd, User32.SW_HIDE);
        _visible = false;
    }

    /// <summary>Puts the window back at the top of the topmost band (no move, no resize, no activation).</summary>
    public void ReassertTopmost()
    {
        if (_visible)
        {
            User32.SetWindowPos(
                _hwnd,
                User32.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                OverlayWindowPolicy.PositionFlags | User32.SWP_NOMOVE | User32.SWP_NOSIZE);
        }
    }

    /// <summary>True once after a display change (resolution, monitor added/removed, DPI); clears the flag.</summary>
    public bool ConsumeGeometryChanged()
    {
        var changed = _geometryChanged;
        _geometryChanged = false;
        return changed;
    }

    /// <summary>True once after another window came to the foreground; clears the flag.</summary>
    public bool ConsumeZOrderChanged()
    {
        var changed = _zOrderChanged;
        _zOrderChanged = false;
        return changed;
    }

    /// <summary>
    /// Sleeps until <paramref name="handle"/> is signalled, a message arrives, or <paramref name="timeout"/>
    /// elapses, then dispatches every pending message. Returns <see langword="true"/> if the handle was signalled.
    /// </summary>
    public bool WaitAndPump(WaitHandle handle, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ObjectDisposedException.ThrowIf(_hwnd == IntPtr.Zero, this);
        var safeHandle = handle.SafeWaitHandle;
        var addedReference = false;
        uint result;
        try
        {
            safeHandle.DangerousAddRef(ref addedReference);
            var raw = safeHandle.DangerousGetHandle();
            result = User32.MsgWaitForMultipleObjectsEx(
                1,
                &raw,
                (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue),
                User32.QS_ALLINPUT,
                User32.MWMO_INPUTAVAILABLE);
        }
        finally
        {
            if (addedReference)
            {
                safeHandle.DangerousRelease();
            }
        }

        if (result == User32.WAIT_FAILED)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "MsgWaitForMultipleObjectsEx failed.");
        }

        PumpMessages();
        return result == User32.WAIT_OBJECT_0;
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            PumpMessages();
        }

        fixed (char* className = ClassName)
        {
            User32.UnregisterClass(className, _instance);
        }

        if (ReferenceEquals(t_current, this))
        {
            t_current = null;
        }
    }

    private static void PumpMessages()
    {
        User32.Msg message;
        while (User32.PeekMessage(&message, IntPtr.Zero, 0, 0, User32.PM_REMOVE))
        {
            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }
    }

    private static void RegisterWindowClass(IntPtr instance)
    {
        fixed (char* className = ClassName)
        {
            var windowClass = new User32.WndClassEx
            {
                Size = (uint)sizeof(User32.WndClassEx),
                WndProc = &WindowProcedure,
                Instance = instance,
                ClassName = className,
            };

            if (User32.RegisterClassEx(&windowClass) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(error, "RegisterClassEx failed for the overlay window class.");
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case User32.WM_MOUSEACTIVATE:
                return User32.MA_NOACTIVATE;

            case User32.WM_NCHITTEST:
                return User32.HTTRANSPARENT;

            case User32.WM_ERASEBKGND:
                // Nothing to erase: all content is the DirectComposition visual.
                return 1;

            case User32.WM_DISPLAYCHANGE:
            case User32.WM_DPICHANGED:
                MarkGeometryChanged();
                return IntPtr.Zero;

            case User32.WM_SETTINGCHANGE:
                // Taskbar moves and work-area changes arrive here; the monitor rectangle may have changed too.
                MarkGeometryChanged();
                return User32.DefWindowProc(hwnd, message, wParam, lParam);

            default:
                return User32.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint eventTime)
    {
        if (t_current is { } window)
        {
            window._zOrderChanged = true;
        }
    }

    private static void MarkGeometryChanged()
    {
        if (t_current is { } window)
        {
            window._geometryChanged = true;
        }
    }
}
