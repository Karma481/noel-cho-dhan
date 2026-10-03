using System.Runtime.CompilerServices;
using AmbientLight.Core.Settings;
using AmbientLight.Serial.Ports;

namespace AmbientLight.Serial.Tests;

public sealed class SerialPlatformTests
{
    [Theory]
    [InlineData(2, SerialPortError.NotFound)]       // ERROR_FILE_NOT_FOUND: no such COM port
    [InlineData(3, SerialPortError.NotFound)]       // ERROR_PATH_NOT_FOUND
    [InlineData(1167, SerialPortError.NotFound)]    // ERROR_DEVICE_NOT_CONNECTED
    [InlineData(31, SerialPortError.NotFound)]      // ERROR_GEN_FAILURE: device mid-removal
    [InlineData(5, SerialPortError.Busy)]           // ERROR_ACCESS_DENIED: another app holds the port
    [InlineData(32, SerialPortError.Busy)]          // ERROR_SHARING_VIOLATION
    public void OpenErrors_AreClassified(int win32Error, SerialPortError expected)
    {
        Assert.Equal(expected, SerialErrorClassifier.FromOpenError(win32Error));
    }

    [Theory]
    [InlineData(87, SerialPortError.InvalidConfiguration)] // ERROR_INVALID_PARAMETER: baud rate rejected
    [InlineData(22, SerialPortError.Disconnected)]
    public void ConfigureErrors_AreClassified(int win32Error, SerialPortError expected)
    {
        Assert.Equal(expected, SerialErrorClassifier.FromConfigureError(win32Error));
    }

    [Theory]
    [InlineData(22)]   // ERROR_BAD_COMMAND: typical CH340/CP210x unplug
    [InlineData(5)]    // ERROR_ACCESS_DENIED: typical usbser.sys (ESP32-S3 native USB) unplug
    [InlineData(995)]  // ERROR_OPERATION_ABORTED
    [InlineData(1167)] // ERROR_DEVICE_NOT_CONNECTED
    public void EveryWriteError_TriggersAReconnect(int win32Error)
    {
        Assert.Equal(SerialPortError.Disconnected, SerialErrorClassifier.FromWriteError(win32Error));
    }

    [Fact]
    public void Win32Structs_MatchTheirWindowsSizes()
    {
        Assert.Equal(28, Unsafe.SizeOf<Win32SerialPort.Dcb>());
        Assert.Equal(20, Unsafe.SizeOf<Win32SerialPort.CommTimeouts>());
    }

    [Fact]
    public void StandardBaudRates_AreTheSupportedPresets()
    {
        Assert.Equal([115_200, 460_800, 921_600, 1_000_000, 2_000_000], SerialSettings.StandardBaudRates);
        Assert.Contains(new SerialSettings().BaudRate, SerialSettings.StandardBaudRates);
    }

    [Fact]
    public void NonStandardBaudRate_IsAllowedWithAWarning()
    {
        var settings = new AppSettings { Serial = new SerialSettings { Enabled = true, PortName = "COM3", BaudRate = 250_000 } };

        var issue = Assert.Single(settings.Validate(), issue => issue.Path == "serial.baudRate");

        Assert.Equal(SettingsIssueSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData(115_200, 100, 26.56)]
    [InlineData(1_000_000, 100, 3.06)]
    [InlineData(2_000_000, 300, 4.53)]
    public void TransmitTime_FollowsFrameSizeAndBaudRate(int baudRate, int leds, double expectedMs)
    {
        Assert.Equal(expectedMs, new SerialSettings { BaudRate = baudRate }.EstimateTransmitTime(leds).TotalMilliseconds, precision: 2);
    }
}
