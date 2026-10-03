namespace AmbientLight.Serial.Ports;

/// <summary>Why a serial operation failed, which decides how the output reacts.</summary>
public enum SerialPortError
{
    /// <summary>No such port right now (USB device unplugged, wrong name). Retried quickly.</summary>
    NotFound = 0,

    /// <summary>Another application has the port open (serial monitor, flasher). Retried.</summary>
    Busy = 1,

    /// <summary>The device went away while open (USB unplugged, driver reset). Reconnect.</summary>
    Disconnected = 2,

    /// <summary>A write did not complete in time (stalled device or flow control). Reconnect.</summary>
    Timeout = 3,

    /// <summary>The driver rejected the configuration, typically an unsupported baud rate. Retried slowly.</summary>
    InvalidConfiguration = 4,
}

/// <summary>A serial operation failure with its classification.</summary>
public sealed class SerialPortException : IOException
{
    /// <summary>Creates an exception with a generic message.</summary>
    public SerialPortException()
        : this(SerialPortError.Disconnected, "Serial port failure.")
    {
    }

    /// <summary>Creates a disconnection exception.</summary>
    public SerialPortException(string message)
        : this(SerialPortError.Disconnected, message)
    {
    }

    /// <summary>Creates a disconnection exception wrapping <paramref name="innerException"/>.</summary>
    public SerialPortException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = SerialPortError.Disconnected;
    }

    /// <summary>Creates an exception of the given kind.</summary>
    public SerialPortException(SerialPortError error, string message, int nativeErrorCode = 0)
        : base(message)
    {
        Error = error;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Classification of the failure.</summary>
    public SerialPortError Error { get; }

    /// <summary>Win32 error code behind the failure, 0 when not applicable.</summary>
    public int NativeErrorCode { get; }
}

/// <summary>An open serial port the output stage writes frames to.</summary>
public interface ISerialPort : IDisposable
{
    /// <summary>Writes all of <paramref name="data"/> or throws <see cref="SerialPortException"/>.</summary>
    void Write(ReadOnlySpan<byte> data);
}

/// <summary>Opens serial ports; replaced by a fake in tests.</summary>
public interface ISerialPortFactory
{
    /// <summary>
    /// Opens <paramref name="portName"/> exclusively at <paramref name="baudRate"/> 8N1 with no flow control.
    /// A write that has not completed within <paramref name="writeTimeout"/> fails with <see cref="SerialPortError.Timeout"/>.
    /// </summary>
    /// <exception cref="SerialPortException">The port is missing, busy or rejects the configuration.</exception>
    ISerialPort Open(string portName, int baudRate, TimeSpan writeTimeout);

    /// <summary>Names of the serial ports currently present, for the settings UI.</summary>
    IReadOnlyList<string> GetPortNames();
}
