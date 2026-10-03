using AmbientLight.Serial.Ports;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Serial;

/// <summary>Source-generated, allocation-free log messages for the serial output.</summary>
internal static partial class SerialLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Serial output started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Serial output stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Connected to {PortName} at {BaudRate} baud")]
    public static partial void Connected(ILogger logger, string portName, int baudRate);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Warning,
        Message = "Lost {PortName} ({Error}): {Reason}. Reconnecting automatically")]
    public static partial void Disconnected(ILogger logger, string portName, SerialPortError error, string reason);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning,
        Message = "Cannot open {PortName} ({Status}): {Reason}. Retrying in the background")]
    public static partial void OpenFailed(ILogger logger, string portName, SerialOutputStatus status, string reason);

    [LoggerMessage(EventId = 4020, Level = LogLevel.Debug, Message = "Closing the serial port failed")]
    public static partial void CloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Debug, Message = "Could not send the blackout frame on stop")]
    public static partial void BlackoutFailed(ILogger logger, Exception exception);
}
