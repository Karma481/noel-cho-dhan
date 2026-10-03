using AmbientLight.Overlay.Interop;
using AmbientLight.Overlay.Window;

namespace AmbientLight.Overlay.Tests;

public sealed class WindowPolicyTests
{
    [Theory]
    [InlineData(0x00000020u, "WS_EX_TRANSPARENT")]
    [InlineData(0x00080000u, "WS_EX_LAYERED")]
    [InlineData(0x00000080u, "WS_EX_TOOLWINDOW")]
    [InlineData(0x08000000u, "WS_EX_NOACTIVATE")]
    [InlineData(0x00000008u, "WS_EX_TOPMOST")]
    [InlineData(0x00200000u, "WS_EX_NOREDIRECTIONBITMAP")]
    public void ExtendedStyle_ContainsEveryRequiredFlag(uint flag, string name)
    {
        Assert.True((OverlayWindowPolicy.ExtendedStyle & flag) == flag, $"{name} is missing.");
    }

    [Fact]
    public void ExtendedStyle_NeverPutsTheOverlayInTheTaskbar()
    {
        Assert.Equal(0u, OverlayWindowPolicy.ExtendedStyle & User32.WS_EX_APPWINDOW);
    }

    [Fact]
    public void Constants_MatchWinUserH()
    {
        Assert.Equal(0x00000020u, User32.WS_EX_TRANSPARENT);
        Assert.Equal(0x00080000u, User32.WS_EX_LAYERED);
        Assert.Equal(0x00000080u, User32.WS_EX_TOOLWINDOW);
        Assert.Equal(0x08000000u, User32.WS_EX_NOACTIVATE);
        Assert.Equal(0x00000011u, User32.WDA_EXCLUDEFROMCAPTURE);
        Assert.Equal(new IntPtr(-1), User32.HWND_TOPMOST);
        Assert.Equal(-1, User32.HTTRANSPARENT);
        Assert.Equal(3, User32.MA_NOACTIVATE);
        Assert.Equal(0x80000000u, OverlayWindowPolicy.Style);
    }

    [Fact]
    public void EveryRepositioning_IsNonActivating()
    {
        Assert.Equal(User32.SWP_NOACTIVATE, OverlayWindowPolicy.PositionFlags & User32.SWP_NOACTIVATE);
    }

    [Theory]
    [InlineData(6, 3, 9600, false)]     // Windows 8.1
    [InlineData(10, 0, 18363, false)]   // Windows 10 1909
    [InlineData(10, 0, 19041, true)]    // Windows 10 2004: WDA_EXCLUDEFROMCAPTURE introduced
    [InlineData(10, 0, 19045, true)]    // Windows 10 22H2
    [InlineData(10, 0, 26100, true)]    // Windows 11 24H2
    public void CaptureExclusion_RequiresWindows10Version2004(int major, int minor, int build, bool supported)
    {
        Assert.Equal(supported, OverlayWindowPolicy.SupportsCaptureExclusion(new Version(major, minor, build)));
    }

    [Theory]
    [InlineData(CaptureExclusion.Excluded, true)]
    [InlineData(CaptureExclusion.UnsupportedOperatingSystem, false)]
    [InlineData(CaptureExclusion.Failed, false)]
    public void Overlay_IsShownOnlyWhenExcludedFromCapture(CaptureExclusion exclusion, bool mayShow)
    {
        Assert.Equal(mayShow, OverlayWindowPolicy.MayShow(exclusion));
    }

    [Fact]
    public void MonitorSelector_MatchesTheCapturedOutputByName_CaseInsensitively()
    {
        var monitors = Monitors();

        Assert.Equal(@"\\.\DISPLAY2", MonitorSelector.Select(monitors, @"\\.\display2")!.DeviceName);
        Assert.Equal(new ScreenRect(-2560, -200, 2560, 1440), MonitorSelector.Select(monitors, @"\\.\DISPLAY2")!.Bounds);
    }

    [Fact]
    public void MonitorSelector_NullMeansPrimary()
    {
        Assert.Equal(@"\\.\DISPLAY1", MonitorSelector.Select(Monitors(), null)!.DeviceName);
    }

    [Fact]
    public void MonitorSelector_FallsBackToTheOrigin_WhenNoPrimaryFlag()
    {
        MonitorDescriptor[] monitors =
        [
            new(@"\\.\DISPLAY3", new ScreenRect(1920, 0, 1920, 1080), false),
            new(@"\\.\DISPLAY4", new ScreenRect(0, 0, 1920, 1080), false),
        ];

        Assert.Equal(@"\\.\DISPLAY4", MonitorSelector.Select(monitors, null)!.DeviceName);
    }

    [Fact]
    public void MonitorSelector_ReturnsNull_ForADisconnectedMonitor()
    {
        Assert.Null(MonitorSelector.Select(Monitors(), @"\\.\DISPLAY9"));
        Assert.Null(MonitorSelector.Select([], null));
    }

    [Fact]
    public unsafe void Win32Structs_MatchTheirWindowsX64Sizes()
    {
        Assert.True(Environment.Is64BitProcess, "Layouts are pinned for 64-bit processes.");
        Assert.Equal(80, sizeof(User32.WndClassEx));
        Assert.Equal(48, sizeof(User32.Msg));
        Assert.Equal(104, sizeof(User32.MonitorInfoEx));
        Assert.Equal(16, sizeof(User32.Rect));
        Assert.Equal(8, sizeof(User32.Point));
    }

    private static MonitorDescriptor[] Monitors() =>
    [
        new(@"\\.\DISPLAY1", new ScreenRect(0, 0, 3840, 2160), true),
        new(@"\\.\DISPLAY2", new ScreenRect(-2560, -200, 2560, 1440), false),
    ];
}
