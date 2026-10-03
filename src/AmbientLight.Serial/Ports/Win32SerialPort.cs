using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AmbientLight.Serial.Ports;

/// <summary>
/// A COM port driven directly through the Win32 comm API (<c>CreateFile</c>, <c>SetCommState</c>,
/// <c>SetCommTimeouts</c>, synchronous <c>WriteFile</c>).
/// </summary>
/// <remarks>
/// <para><c>System.IO.Ports.SerialPort</c> is deliberately not used here:</para>
/// <list type="bullet">
/// <item>its Windows <c>SerialStream.Write</c> allocates an async-result object and a
/// <c>NativeOverlapped</c> per call, so it cannot be allocation-free;</item>
/// <item>it always runs a background event-loop thread waiting on comm events, the historical source of
/// unhandled exceptions when a USB-serial adapter is unplugged.</item>
/// </list>
/// <para>
/// Here a write is a single synchronous <c>WriteFile</c> on the caller's thread from a pinned span, bounded
/// by a comm write timeout so a stalled device cannot hang the output thread. The handle is a
/// <see cref="SafeFileHandle"/>, so it is closed exactly once even if the device has vanished.
/// </para>
/// <para>
/// DTR and RTS are held inactive: on ESP32 boards they drive the auto-reset/boot-mode circuit, and
/// asserting them would reboot the controller or put it into the bootloader.
/// </para>
/// </remarks>
internal sealed unsafe partial class Win32SerialPort : ISerialPort
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;
    private const uint MaxDword = 0xFFFFFFFF;

    // DCB field values.
    private const byte EightDataBits = 8;
    private const byte NoParity = 0;
    private const byte OneStopBit = 0;

    // DCB bit field: fBinary = 1, everything else (parity check, CTS/DSR flow control, DTR_CONTROL_DISABLE,
    // XON/XOFF, RTS_CONTROL_DISABLE, fAbortOnError) = 0.
    private const uint DcbFlagsBinaryNoFlowControl = 0x00000001;

    private readonly SafeFileHandle _handle;
    private readonly string _portName;

    private Win32SerialPort(SafeFileHandle handle, string portName)
    {
        _handle = handle;
        _portName = portName;
    }

    /// <summary>Opens and configures <paramref name="portName"/>.</summary>
    public static Win32SerialPort Open(string portName, int baudRate, TimeSpan writeTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudRate);

        // COM10 and above are only reachable through the device namespace; the prefix works for all.
        var path = portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? portName : @"\\.\" + portName;
        var handle = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new SerialPortException(
                SerialErrorClassifier.FromOpenError(error),
                $"Cannot open {portName} (Win32 error {error}).",
                error);
        }

        try
        {
            Configure(handle, portName, baudRate, writeTimeout);
            return new Win32SerialPort(handle, portName);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Port names from <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c>, the registry key Windows keeps for present COM ports.</summary>
    public static IReadOnlyList<string> GetPortNames()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames()
            .Select(name => key.GetValue(name) as string)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        uint written;
        bool succeeded;
        fixed (byte* pointer = data)
        {
            succeeded = WriteFile(_handle, pointer, (uint)data.Length, out written, IntPtr.Zero);
        }

        if (!succeeded)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new SerialPortException(
                SerialErrorClassifier.FromWriteError(error),
                $"Writing to {_portName} failed (Win32 error {error}).",
                error);
        }

        // With comm timeouts, an expired write still returns success, only with fewer bytes written.
        if (written != data.Length)
        {
            throw new SerialPortException(
                SerialPortError.Timeout,
                $"Writing to {_portName} timed out after {written} of {data.Length} bytes.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    private static void Configure(SafeFileHandle handle, string portName, int baudRate, TimeSpan writeTimeout)
    {
        var dcb = new Dcb { Length = (uint)sizeof(Dcb) };
        if (!GetCommState(handle, ref dcb))
        {
            ThrowConfigure(portName, "GetCommState");
        }

        dcb.BaudRate = (uint)baudRate;
        dcb.Flags = DcbFlagsBinaryNoFlowControl;
        dcb.ByteSize = EightDataBits;
        dcb.Parity = NoParity;
        dcb.StopBits = OneStopBit;
        if (!SetCommState(handle, ref dcb))
        {
            ThrowConfigure(portName, $"SetCommState({baudRate} baud)");
        }

        var timeouts = new CommTimeouts
        {
            // Reads return immediately with whatever is buffered; the output never blocks on input.
            ReadIntervalTimeout = MaxDword,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = 0,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = (uint)Math.Clamp(writeTimeout.TotalMilliseconds, 1, int.MaxValue),
        };
        if (!SetCommTimeouts(handle, ref timeouts))
        {
            ThrowConfigure(portName, "SetCommTimeouts");
        }

        // Drop anything a previous session left queued (and the firmware's boot greeting).
        PurgeComm(handle, PurgeTxClear | PurgeRxClear);
    }

    private static void ThrowConfigure(string portName, string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new SerialPortException(
            SerialErrorClassifier.FromConfigureError(error),
            $"{operation} failed on {portName} (Win32 error {error}).",
            error);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCommState(SafeFileHandle file, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommState(SafeFileHandle file, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PurgeComm(SafeFileHandle file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, byte* buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, IntPtr overlapped);

    // DCB (28 bytes). The C bit fields fBinary..fAbortOnError are packed into Flags.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EventChar;
        public ushort Reserved1;
    }

    // COMMTIMEOUTS (20 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }
}

/// <summary>Opens real COM ports through <see cref="Win32SerialPort"/>.</summary>
public sealed class Win32SerialPortFactory : ISerialPortFactory
{
    /// <inheritdoc />
    public ISerialPort Open(string portName, int baudRate, TimeSpan writeTimeout) =>
        Win32SerialPort.Open(portName, baudRate, writeTimeout);

    /// <inheritdoc />
    public IReadOnlyList<string> GetPortNames() => Win32SerialPort.GetPortNames();
}
