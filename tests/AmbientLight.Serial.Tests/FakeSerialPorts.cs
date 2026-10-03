using AmbientLight.Serial.Ports;

namespace AmbientLight.Serial.Tests;

/// <summary>A scriptable port factory that records every port it hands out.</summary>
internal sealed class FakeSerialPortFactory : ISerialPortFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeSerialPort> _opened = [];

    /// <summary>When set, <see cref="Open"/> fails with this error (device unplugged, port busy...).</summary>
    public SerialPortError? OpenFailure { get; set; }

    public int OpenAttempts { get; private set; }

    public string? LastPortName { get; private set; }

    public int LastBaudRate { get; private set; }

    public TimeSpan LastWriteTimeout { get; private set; }

    public IReadOnlyList<FakeSerialPort> Opened
    {
        get
        {
            lock (_gate)
            {
                return _opened.ToArray();
            }
        }
    }

    /// <summary>Ports opened and not yet disposed: must never exceed one, and be zero after shutdown.</summary>
    public int LivePorts
    {
        get
        {
            lock (_gate)
            {
                return _opened.Count(port => !port.IsDisposed);
            }
        }
    }

    public FakeSerialPort? Current
    {
        get
        {
            lock (_gate)
            {
                return _opened.LastOrDefault(port => !port.IsDisposed);
            }
        }
    }

    public ISerialPort Open(string portName, int baudRate, TimeSpan writeTimeout)
    {
        lock (_gate)
        {
            OpenAttempts++;
            LastPortName = portName;
            LastBaudRate = baudRate;
            LastWriteTimeout = writeTimeout;
            if (OpenFailure is { } failure)
            {
                throw new SerialPortException(failure, $"Simulated {failure} opening {portName}.");
            }

            var port = new FakeSerialPort(portName, baudRate);
            _opened.Add(port);
            return port;
        }
    }

    public IReadOnlyList<string> GetPortNames() => ["COM3", "COM7"];
}

/// <summary>A port that records writes and can be told to fail like an unplugged or stalled device.</summary>
internal sealed class FakeSerialPort : ISerialPort
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _writes = [];

    public FakeSerialPort(string portName, int baudRate)
    {
        PortName = portName;
        BaudRate = baudRate;
    }

    public string PortName { get; }

    public int BaudRate { get; }

    /// <summary>When set, the next writes fail with this error.</summary>
    public SerialPortError? WriteFailure { get; set; }

    public bool IsDisposed { get; private set; }

    public int DisposeCount { get; private set; }

    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (WriteFailure is { } failure)
        {
            throw new SerialPortException(failure, $"Simulated {failure} writing to {PortName}.");
        }

        lock (_gate)
        {
            _writes.Add(data.ToArray());
        }
    }

    public void Dispose()
    {
        IsDisposed = true;
        DisposeCount++;
    }
}

/// <summary>A port whose writes only count bytes, for allocation measurements.</summary>
internal sealed class CountingSerialPort : ISerialPort, ISerialPortFactory
{
    public long BytesWritten { get; private set; }

    public ISerialPort Open(string portName, int baudRate, TimeSpan writeTimeout) => this;

    public IReadOnlyList<string> GetPortNames() => [];

    public void Write(ReadOnlySpan<byte> data) => BytesWritten += data.Length;

    public void Dispose()
    {
    }
}

/// <summary>Splits a byte stream into Adalight frames the way the firmware parser does (header checksum, resync).</summary>
internal static class AdalightStreamDecoder
{
    public static List<byte[]> DecodePayloads(ReadOnlySpan<byte> stream)
    {
        var payloads = new List<byte[]>();
        var i = 0;
        while (i + 6 <= stream.Length)
        {
            if (stream[i] != 'A' || stream[i + 1] != 'd' || stream[i + 2] != 'a')
            {
                i++;
                continue;
            }

            var high = stream[i + 3];
            var low = stream[i + 4];
            if (stream[i + 5] != (byte)(high ^ low ^ 0x55))
            {
                i++;
                continue;
            }

            var length = (((high << 8) | low) + 1) * 3;
            if (i + 6 + length > stream.Length)
            {
                break;
            }

            payloads.Add(stream.Slice(i + 6, length).ToArray());
            i += 6 + length;
        }

        return payloads;
    }
}
