using System.Diagnostics;
using AmbientLight.Core.Settings;
using AmbientLight.Serial.Ports;
using AmbientLight.Serial.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmbientLight.Serial.Tests;

public sealed class SerialOutputControllerTests : IDisposable
{
    private static readonly SerialSettings Enabled = new()
    {
        Enabled = true,
        PortName = "COM3",
        BaudRate = 1_000_000,
        MaxRefreshHz = 60,
        KeepAliveMs = 500,
    };

    private readonly FakeSerialPortFactory _factory = new();
    private readonly SerialOutputController _controller;
    private readonly long _now = Stopwatch.Frequency;

    public SerialOutputControllerTests()
    {
        _controller = new SerialOutputController(_factory, NullLogger.Instance);
    }

    [Fact]
    public void Disabled_NeverOpensAPort()
    {
        _controller.SubmitFrame([1, 2, 3]);

        _controller.Step(_now, Enabled with { Enabled = false });
        _controller.Step(_now, Enabled with { PortName = " " });

        Assert.Equal(0, _factory.OpenAttempts);
        Assert.Equal(SerialOutputStatus.Disabled, _controller.Status);
    }

    [Fact]
    public void FirstFrame_OpensThePortWithTheSettings_AndWritesTheEncodedFrame()
    {
        _controller.SubmitFrame([0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF]);

        _controller.Step(_now, Enabled);

        Assert.Equal(SerialOutputStatus.Connected, _controller.Status);
        Assert.Equal("COM3", _factory.LastPortName);
        Assert.Equal(1_000_000, _factory.LastBaudRate);
        Assert.Equal(SerialOutputController.WriteTimeoutFor(1_000_000), _factory.LastWriteTimeout);
        var write = Assert.Single(_factory.Current!.Writes);
        Assert.Equal(new byte[] { 0x41, 0x64, 0x61, 0x00, 0x02, 0x57, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF }, write);
    }

    [Fact]
    public void FramesFasterThanTheRefreshRate_AreCoalesced_AndTheNewestIsSent()
    {
        _controller.SubmitFrame([1, 1, 1]);
        _controller.Step(_now, Enabled);

        _controller.SubmitFrame([2, 2, 2]);
        var wait = _controller.Step(At(5), Enabled);
        Assert.InRange(wait.TotalMilliseconds, 11, 12.5); // 16.7 ms interval - 5 ms

        _controller.SubmitFrame([3, 3, 3]);
        _controller.Step(At(10), Enabled);
        _controller.Step(At(17), Enabled);

        var payloads = _factory.Current!.Writes.Select(w => w[6..]).ToArray();
        Assert.Equal(2, payloads.Length);
        Assert.Equal(new byte[] { 1, 1, 1 }, payloads[0]);
        Assert.Equal(new byte[] { 3, 3, 3 }, payloads[1]); // frame 2 was superseded, never queued
    }

    [Fact]
    public void StaticScreen_RepeatsTheLastFrameAsKeepAlive()
    {
        _controller.SubmitFrame([9, 8, 7]);
        _controller.Step(_now, Enabled);

        var wait = _controller.Step(At(400), Enabled);
        Assert.Single(_factory.Current!.Writes);
        Assert.InRange(wait.TotalMilliseconds, 99, 101);

        _controller.Step(At(500), Enabled);
        _controller.Step(At(1000), Enabled);

        var writes = _factory.Current.Writes;
        Assert.Equal(3, writes.Count);
        Assert.All(writes, write => Assert.Equal(writes[0], write));
        Assert.Equal(1, _controller.Statistics.FramesSent);
        Assert.Equal(2, _controller.Statistics.KeepAlivesSent);
    }

    [Fact]
    public void KeepAlive_AlwaysBeatsTheFirmwareWatchdog()
    {
        // The largest allowed keep-alive leaves a quarter of the watchdog as margin for USB latency.
        var maxKeepAlive = Enabled with { KeepAliveMs = SerialSettings.FirmwareWatchdogMs * 3 / 4 };
        var tooSlow = Enabled with { KeepAliveMs = SerialSettings.FirmwareWatchdogMs };

        Assert.DoesNotContain(new AppSettings { Serial = maxKeepAlive }.Validate(), issue => issue.Path == "serial.keepAliveMs");
        Assert.Contains(new AppSettings { Serial = tooSlow }.Validate(), issue => issue.Path == "serial.keepAliveMs");
    }

    [Fact]
    public void Unplug_ReleasesThePort_RetriesWithBackoff_AndResumesOnReplug()
    {
        _controller.SubmitFrame([1, 2, 3]);
        _controller.Step(_now, Enabled);
        var first = _factory.Current!;

        // Cable pulled: the next write fails.
        first.WriteFailure = SerialPortError.Disconnected;
        _controller.SubmitFrame([4, 5, 6]);
        var wait = _controller.Step(At(20), Enabled);

        Assert.True(first.IsDisposed);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(SerialOutputStatus.Reconnecting, _controller.Status);
        Assert.Equal(100, wait.TotalMilliseconds, precision: 0);

        // The device is gone: retries back off 100 -> 200 -> 400 -> 800 -> 1000 -> 1000 ms.
        _factory.OpenFailure = SerialPortError.NotFound;
        var elapsed = 20.0;
        double[] expectedDelays = [200, 400, 800, 1000, 1000];
        elapsed += 100;
        foreach (var expected in expectedDelays)
        {
            wait = _controller.Step(At(elapsed), Enabled);
            Assert.Equal(SerialOutputStatus.PortNotFound, _controller.Status);
            Assert.Equal(expected, wait.TotalMilliseconds, precision: 0);
            Assert.Equal(0, _factory.LivePorts);
            elapsed += expected;
        }

        // Plugged back in: reconnects and sends the newest frame straight away.
        _factory.OpenFailure = null;
        _controller.Step(At(elapsed), Enabled);

        Assert.Equal(SerialOutputStatus.Connected, _controller.Status);
        Assert.Equal(1, _factory.LivePorts);
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.Single(_factory.Current!.Writes)[6..]);
        Assert.Equal(2, _controller.Statistics.Connections);
        Assert.Equal(1, _controller.Statistics.WriteFailures);
    }

    [Theory]
    [InlineData(SerialPortError.Busy, SerialOutputStatus.PortBusy, 100)]
    [InlineData(SerialPortError.NotFound, SerialOutputStatus.PortNotFound, 100)]
    [InlineData(SerialPortError.InvalidConfiguration, SerialOutputStatus.InvalidConfiguration, 5000)]
    public void OpenFailures_MapToAStatus_AndARetryDelay(SerialPortError error, SerialOutputStatus status, double retryMs)
    {
        _factory.OpenFailure = error;
        _controller.SubmitFrame([1, 2, 3]);

        var wait = _controller.Step(_now, Enabled);

        Assert.Equal(status, _controller.Status);
        Assert.Equal(retryMs, wait.TotalMilliseconds, precision: 0);
        Assert.Equal(0, _factory.LivePorts);
    }

    [Fact]
    public void StalledWrite_IsTreatedAsADisconnect()
    {
        _controller.SubmitFrame([1, 2, 3]);
        _controller.Step(_now, Enabled);
        _factory.Current!.WriteFailure = SerialPortError.Timeout;

        _controller.Step(At(500), Enabled);

        Assert.Equal(SerialOutputStatus.Reconnecting, _controller.Status);
        Assert.Equal(0, _factory.LivePorts);
    }

    [Fact]
    public void ChangingPortOrBaudRate_ReopensImmediately()
    {
        _controller.SubmitFrame([1, 2, 3]);
        _controller.Step(_now, Enabled);
        var original = _factory.Current!;

        _controller.Step(At(1), Enabled with { PortName = "COM7" });
        Assert.True(original.IsDisposed);
        Assert.Equal("COM7", _factory.Current!.PortName);

        _controller.Step(At(2), Enabled with { PortName = "COM7", BaudRate = 2_000_000 });
        Assert.Equal(2_000_000, _factory.Current!.BaudRate);
        Assert.Equal(1, _factory.LivePorts);
        Assert.Equal(3, _factory.Opened.Count);

        // Every new connection re-sends the current frame at once.
        Assert.All(_factory.Opened, port => Assert.Single(port.Writes));
    }

    [Fact]
    public void Shutdown_SendsABlackFrameOfTheSameLength_ThenCloses()
    {
        _controller.SubmitFrame([10, 20, 30, 40, 50, 60]);
        _controller.Step(_now, Enabled);
        var port = _factory.Current!;

        _controller.Shutdown(blackout: true);

        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0x00, 0x01, 0x54, 0, 0, 0, 0, 0, 0 }, port.Writes[^1]);
        Assert.True(port.IsDisposed);
        Assert.Equal(SerialOutputStatus.Stopped, _controller.Status);
    }

    [Fact]
    public void Shutdown_WithoutBlackout_OrWithoutAPort_JustCloses()
    {
        _controller.SubmitFrame([1, 2, 3]);
        _controller.Step(_now, Enabled);
        var port = _factory.Current!;

        _controller.Shutdown(blackout: false);
        _controller.Shutdown(blackout: true);

        Assert.Single(port.Writes);
        Assert.Equal(0, _factory.LivePorts);
    }

    [Fact]
    public void Shutdown_NeverThrows_WhenTheDeviceIsAlreadyGone()
    {
        _controller.SubmitFrame([1, 2, 3]);
        _controller.Step(_now, Enabled);
        _factory.Current!.WriteFailure = SerialPortError.Disconnected;

        _controller.Shutdown(blackout: true);

        Assert.Equal(0, _factory.LivePorts);
    }

    [Fact]
    public void RandomFailures_NeverLeakPorts_AndEveryWriteIsAValidFrame()
    {
        var random = new Random(2024);
        var ports = new HashSet<FakeSerialPort>();
        double elapsed = 0;

        for (var step = 0; step < 5000; step++)
        {
            elapsed += random.Next(1, 120);
            _factory.OpenFailure = random.Next(10) switch
            {
                0 => SerialPortError.NotFound,
                1 => SerialPortError.Busy,
                _ => null,
            };

            if (_factory.Current is { } current)
            {
                ports.Add(current);
                current.WriteFailure = random.Next(25) == 0 ? SerialPortError.Disconnected : null;
            }

            if (random.Next(3) == 0)
            {
                var leds = random.Next(1, 200);
                var rgb = new byte[leds * 3];
                random.NextBytes(rgb);
                _controller.SubmitFrame(rgb);
            }

            _controller.Step(At(elapsed), Enabled);
            Assert.True(_factory.LivePorts <= 1, $"{_factory.LivePorts} ports open at step {step}.");
        }

        _controller.Shutdown(blackout: true);

        Assert.Equal(0, _factory.LivePorts);
        Assert.True(_controller.Statistics.Connections > 10);
        foreach (var port in _factory.Opened)
        {
            Assert.Equal(1, port.DisposeCount);
            foreach (var write in port.Writes)
            {
                var payload = Assert.Single(AdalightStreamDecoder.DecodePayloads(write));
                Assert.Equal(write.Length - AdalightEncoder.HeaderLength, payload.Length);
            }
        }
    }

    [Fact]
    public void SteadyState_AllocatesNothing()
    {
        var port = new CountingSerialPort();
        using var controller = new SerialOutputController(port, NullLogger.Instance);
        var rgb = new byte[300 * 3];
        var now = Stopwatch.Frequency;
        var frameTicks = Stopwatch.Frequency / 60;

        for (var i = 0; i < 100; i++)
        {
            controller.SubmitFrame(rgb);
            controller.Step(now += frameTicks, Enabled);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            rgb[i % rgb.Length]++;
            if (i % 4 != 0)
            {
                controller.SubmitFrame(rgb); // every fourth tick has no new frame: keep-alive path
            }

            controller.Step(now += frameTicks, Enabled);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(port.BytesWritten > 0);
    }

    [Theory]
    [InlineData(115_200, 634)]   // 2 x 3078 bytes x 10 bits / 115200 + 100 ms
    [InlineData(1_000_000, 200)] // floor
    [InlineData(2_000_000, 200)]
    public void WriteTimeout_CoversTheLargestFrameWithMargin(int baudRate, double expectedMs)
    {
        Assert.Equal(expectedMs, SerialOutputController.WriteTimeoutFor(baudRate).TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void EmptyFrame_IsIgnored()
    {
        _controller.SubmitFrame([]);
        _controller.Step(_now, Enabled);

        Assert.Equal(1, _factory.OpenAttempts);
        Assert.Empty(_factory.Current!.Writes);
    }

    public void Dispose() => _controller.Dispose();

    private long At(double milliseconds) => _now + (long)(milliseconds * Stopwatch.Frequency / 1000);
}
