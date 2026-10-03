using AmbientLight.Capture;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Host.Pipeline;
using AmbientLight.Overlay;
using AmbientLight.Overlay.Window;
using AmbientLight.Processing;
using AmbientLight.Serial;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.DXGI;

namespace AmbientLight.Host.Tests;

public sealed class StatusTextTests
{
    private static readonly DisplayOutputInfo Laptop = new(
        @"\\.\DISPLAY1",
        "Intel(R) Iris(R) Xe Graphics",
        1920,
        1080,
        ModeRotation.Identity,
        ColorSpaceType.RgbFullG22NoneP709,
        300,
        0);

    [Theory]
    [InlineData(PipelineMode.Running, false, "Running")]
    [InlineData(PipelineMode.Running, true, "LEDs only (overlay paused for fullscreen game)")]
    [InlineData(PipelineMode.Paused, false, "Paused")]
    [InlineData(PipelineMode.SuspendedForFullscreen, true, "Overlay paused for a fullscreen game")]
    [InlineData(PipelineMode.Idle, false, "Idle: overlay and LED strip are off")]
    [InlineData(PipelineMode.Stopped, false, "Stopped")]
    public void Describe_SummarizesTheMode(PipelineMode mode, bool overlaySuspended, string expected)
    {
        var state = new PipelineState(new PipelinePlan(false, false, false, false, overlaySuspended, mode), false, false, null);

        Assert.Equal(expected, StatusText.Describe(state));
    }

    [Fact]
    public void Describe_FlagsErrors_AndFitsTheTrayTooltip()
    {
        var state = new PipelineState(PipelinePlan.AllStopped, true, false, "Capture could not start: something long");

        var text = "Ambient Light — " + StatusText.Describe(state);

        Assert.EndsWith("(error, see Settings)", text, StringComparison.Ordinal);
        Assert.True(text.Length < 128);
    }

    [Fact]
    public void Capture_ShowsRateMonitorAndGpu()
    {
        var status = Status(CaptureStatus.Capturing, output: Laptop);

        Assert.Equal("Capture: 60 fps · DISPLAY1 on Intel(R) Iris(R) Xe Graphics", StatusText.DescribeCapture(status, 59.7));
        Assert.Equal("Capture: idle (static screen) · DISPLAY1 on Intel(R) Iris(R) Xe Graphics", StatusText.DescribeCapture(status, 0));
    }

    [Fact]
    public void Capture_MentionsHdrAndProtectedContent()
    {
        var hdr = Laptop with { ColorSpace = ColorSpaceType.RgbFullG2084NoneP2020 };
        var status = Status(CaptureStatus.Capturing, output: hdr, protectedContent: true);

        var text = StatusText.DescribeCapture(status, 30);

        Assert.Contains(" · HDR", text, StringComparison.Ordinal);
        Assert.Contains("protected video", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaptureStatus.Stopped, "Capture: stopped")]
    [InlineData(CaptureStatus.WaitingForDesktop, "Capture: waiting for the desktop (lock screen or UAC prompt)…")]
    public void Capture_DescribesOtherStates(CaptureStatus captureStatus, string expected)
    {
        Assert.Equal(expected, StatusText.DescribeCapture(Status(captureStatus), 0));
    }

    [Fact]
    public void Overlay_ConfirmsCaptureExclusion()
    {
        var status = Status(overlayStatus: OverlayStatus.Running, exclusion: CaptureExclusion.Excluded, overlayMonitor: @"\\.\DISPLAY1");

        Assert.Equal("Overlay: on · DISPLAY1 · hidden from screen capture ✓", StatusText.DescribeOverlay(status));
    }

    [Theory]
    [InlineData(CaptureExclusion.UnsupportedOperatingSystem, "Overlay: hidden (needs Windows 10 2004+ to stay out of screenshots)")]
    [InlineData(CaptureExclusion.Failed, "Overlay: hidden (Windows refused to exclude it from screen capture)")]
    public void Overlay_ExplainsWhyItIsHidden(CaptureExclusion exclusion, string expected)
    {
        var status = Status(overlayStatus: OverlayStatus.CaptureExclusionUnavailable, exclusion: exclusion);

        Assert.Equal(expected, StatusText.DescribeOverlay(status));
    }

    [Fact]
    public void Serial_IsOffWhileItWasNeverStarted()
    {
        Assert.Equal("LED strip: off", StatusText.DescribeSerial(Status(), 0));
    }

    [Theory]
    [InlineData(SerialOutputStatus.Connected, "LED strip: connected · COM3 · 60 fps")]
    [InlineData(SerialOutputStatus.PortNotFound, "LED strip: COM3 not found, plug in the controller")]
    [InlineData(SerialOutputStatus.PortBusy, "LED strip: COM3 is in use by another app")]
    [InlineData(SerialOutputStatus.Disabled, "LED strip: off")]
    public void Serial_DescribesTheConnection(SerialOutputStatus serialStatus, string expected)
    {
        var serial = new SerialOutputStatistics(serialStatus, "COM3", 100, 0, 1000, 1, 0, TimeSpan.Zero);

        Assert.Equal(expected, StatusText.DescribeSerial(Status(serial: serial), 60.2));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY2", "DISPLAY2")]
    [InlineData("DISPLAY2", "DISPLAY2")]
    public void ShortMonitorName_DropsTheDevicePrefix(string deviceName, string expected)
    {
        Assert.Equal(expected, StatusText.ShortMonitorName(deviceName));
    }

    private static PipelineStatus Status(
        CaptureStatus captureStatus = CaptureStatus.Stopped,
        DisplayOutputInfo? output = null,
        bool protectedContent = false,
        OverlayStatus overlayStatus = OverlayStatus.Stopped,
        CaptureExclusion? exclusion = null,
        string? overlayMonitor = null,
        SerialOutputStatistics? serial = null) => new(
            captureStatus,
            new CaptureStatistics(0, 0, 0, 0, 0, 0, TimeSpan.Zero, protectedContent, false, default),
            output,
            false,
            new ProcessingStatistics(0, 0, 0, TimeSpan.Zero, NormalizedRect.Full),
            overlayStatus,
            new OverlayStatistics(0, 0, 0, 0, TimeSpan.Zero, exclusion, overlayMonitor),
            serial);
}

public sealed class RateMeterTests
{
    [Fact]
    public void FirstSample_HasNoRate()
    {
        Assert.Equal(0, new RateMeter().Sample(100, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Rate_IsTheCountDeltaOverTheTimeDelta()
    {
        var meter = new RateMeter();
        meter.Sample(100, TimeSpan.FromSeconds(1));

        Assert.Equal(60, meter.Sample(130, TimeSpan.FromSeconds(1.5)), 6);
    }

    [Fact]
    public void CounterReset_GivesZeroInsteadOfANegativeRate()
    {
        var meter = new RateMeter();
        meter.Sample(500, TimeSpan.FromSeconds(1));

        Assert.Equal(0, meter.Sample(10, TimeSpan.FromSeconds(2)));
        Assert.Equal(20, meter.Sample(30, TimeSpan.FromSeconds(3)), 6);
    }
}

public sealed class PipelineServicesTests
{
    [Fact]
    public void Construction_StartsNothing_AndCreatesNoSerialService()
    {
        using var services = new PipelineServices(new SettingsHolder(new AppSettings()), NullLoggerFactory.Instance);

        var status = services.GetStatus();

        Assert.Null(services.Serial);
        Assert.Null(status.Serial);
        Assert.Equal(CaptureStatus.Stopped, status.CaptureStatus);
        Assert.Equal(OverlayStatus.Stopped, status.OverlayStatus);
        Assert.False(status.ProcessingRunning);
        Assert.False(services.Stages.Serial.IsRunning);
        Assert.Equal(["Capture", "Processing", "Overlay", "Serial"], new[] { services.Stages.Capture, services.Stages.Processing, services.Stages.Overlay, services.Stages.Serial }.Select(stage => stage.Name));
    }

    [Fact]
    public void ProcessingStage_RunsAndStopsItsThread()
    {
        using var services = new PipelineServices(new SettingsHolder(new AppSettings()), NullLoggerFactory.Instance);

        services.Stages.Processing.StartStage();
        Assert.True(services.Processing.IsRunning);

        services.Stages.Processing.StopStage();
        Assert.False(services.Processing.IsRunning);
    }
}
