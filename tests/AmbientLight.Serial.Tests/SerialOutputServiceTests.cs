using System.Diagnostics;
using AmbientLight.Core.Color;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Serial.Ports;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmbientLight.Serial.Tests;

public sealed class SerialOutputServiceTests
{
    [Fact]
    public void FramesFlowToThePort_AndStopBlacksOutAndCloses()
    {
        var settings = new SettingsHolder(new AppSettings
        {
            Serial = new SerialSettings { Enabled = true, PortName = "COM3", BaudRate = 1_000_000 },
        });
        using var input = new LatestValueMailbox<FrameData>(() => new FrameData(LedLayoutSettings.MaxLedCount));
        var factory = new FakeSerialPortFactory();
        using var service = new SerialOutputService(settings, input, factory, NullLogger<SerialOutputService>.Instance);

        service.Start();
        var frame = input.WriteSlot;
        frame.SetZoneCount(2);
        frame.LedColors[0] = new ColorRgb(1, 2, 3);
        frame.LedColors[1] = new ColorRgb(4, 5, 6);
        input.Publish();

        Assert.True(SpinWait.SpinUntil(() => factory.Current?.Writes.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, factory.Current!.Writes[0][6..]);
        Assert.Equal(SerialOutputStatus.Connected, service.GetStatistics().Status);

        var port = factory.Current;
        service.Stop();

        Assert.True(port.IsDisposed);
        Assert.Equal(new byte[6], port.Writes[^1][6..]); // blackout frame
        Assert.Equal(SerialOutputStatus.Stopped, service.GetStatistics().Status);
        Assert.Equal(0, factory.LivePorts);
    }

    [Fact]
    public void UnpluggedAtStartup_KeepsRetrying_WithoutBlockingStop()
    {
        var settings = new SettingsHolder(new AppSettings
        {
            Serial = new SerialSettings { Enabled = true, PortName = "COM9", BaudRate = 1_000_000 },
        });
        using var input = new LatestValueMailbox<FrameData>(() => new FrameData(LedLayoutSettings.MaxLedCount));
        var factory = new FakeSerialPortFactory { OpenFailure = SerialPortError.NotFound };
        using var service = new SerialOutputService(settings, input, factory, NullLogger<SerialOutputService>.Instance);

        service.Start();
        Assert.True(SpinWait.SpinUntil(() => factory.OpenAttempts >= 3, TimeSpan.FromSeconds(5)));
        Assert.Equal(SerialOutputStatus.PortNotFound, service.GetStatistics().Status);

        var stopwatch = Stopwatch.StartNew();
        service.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Stop took {stopwatch.Elapsed}.");
        Assert.Equal(0, factory.LivePorts);
    }

    [Fact]
    public void StartTwice_Throws_AndStopIsIdempotent()
    {
        var settings = new SettingsHolder(new AppSettings());
        using var input = new LatestValueMailbox<FrameData>(() => new FrameData(4));
        using var service = new SerialOutputService(settings, input, new FakeSerialPortFactory(), NullLogger<SerialOutputService>.Instance);

        service.Stop();
        service.Start();
        Assert.Throws<InvalidOperationException>(service.Start);
        service.Stop();
        service.Stop();
        Assert.Equal(SerialOutputStatus.Stopped, service.GetStatistics().Status);
    }
}
