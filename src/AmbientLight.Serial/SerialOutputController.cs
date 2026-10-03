using System.Diagnostics;
using AmbientLight.Core.Settings;
using AmbientLight.Serial.Ports;
using AmbientLight.Serial.Protocol;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Serial;

/// <summary>Connection state of the serial output.</summary>
public enum SerialOutputStatus
{
    /// <summary>The output thread is not running.</summary>
    Stopped = 0,

    /// <summary>Serial output is turned off or no port is configured.</summary>
    Disabled = 1,

    /// <summary>About to open the port.</summary>
    Connecting = 2,

    /// <summary>The port is open and frames are being sent.</summary>
    Connected = 3,

    /// <summary>The port does not exist (device unplugged); polling for it to come back.</summary>
    PortNotFound = 4,

    /// <summary>Another application holds the port; retrying.</summary>
    PortBusy = 5,

    /// <summary>The device dropped out while open (write failed or stalled); reconnecting.</summary>
    Reconnecting = 6,

    /// <summary>The driver rejected the baud rate; retrying slowly in case the setting is changed.</summary>
    InvalidConfiguration = 7,
}

/// <summary>Point-in-time counters of the serial output.</summary>
/// <param name="Status">Connection state.</param>
/// <param name="PortName">Port in use or being retried.</param>
/// <param name="FramesSent">New frames written.</param>
/// <param name="KeepAlivesSent">Repeats of the last frame sent to keep the firmware watchdog fed.</param>
/// <param name="BytesSent">Bytes written in total.</param>
/// <param name="Connections">Successful port opens (the first one plus every reconnect).</param>
/// <param name="WriteFailures">Writes that failed or timed out, each followed by a reconnect.</param>
/// <param name="LastWriteTime">Duration of the most recent write call.</param>
public readonly record struct SerialOutputStatistics(
    SerialOutputStatus Status,
    string? PortName,
    long FramesSent,
    long KeepAlivesSent,
    long BytesSent,
    long Connections,
    long WriteFailures,
    TimeSpan LastWriteTime);

/// <summary>
/// The serial output as a deterministic state machine: the caller supplies the time and the frames, the
/// controller decides when to open, write, keep alive and reconnect. Owned by one thread.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rate limit</b>: at most <see cref="SerialSettings.MaxRefreshHz"/> frames per second. Frames arriving
/// faster are coalesced, and the newest is sent when the interval allows (latest wins, never a queue).</item>
/// <item><b>Keep-alive</b>: on a static screen the last frame is repeated every
/// <see cref="SerialSettings.KeepAliveMs"/> so the firmware watchdog never fades a still picture out.</item>
/// <item><b>Auto-reconnect</b>: any open or write failure disposes the port at once (no handle leaks), then
/// retries with back-off 100 ms → 1 s. A replugged device is picked up within a second, and the latest
/// frame is sent immediately after reconnecting.</item>
/// <item><b>Zero allocation</b>: the frame buffer is sized once for the largest layout; encoding writes into
/// it and keep-alives resend it as is.</item>
/// </list>
/// </remarks>
internal sealed class SerialOutputController : IDisposable
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan InvalidConfigurationRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MinimumWriteTimeout = TimeSpan.FromMilliseconds(200);

    private readonly ISerialPortFactory _factory;
    private readonly ILogger _logger;
    private readonly byte[] _frame = new byte[AdalightEncoder.FrameLength(LedLayoutSettings.MaxLedCount)];

    private int _frameLength;
    private int _ledCount;
    private bool _pending;

    private ISerialPort? _port;
    private string? _portName;
    private int _baudRate;
    private long _lastSentTicks;
    private long _retryAtTicks;
    private TimeSpan _retryDelay = InitialRetryDelay;
    private int _status = (int)SerialOutputStatus.Connecting;

    private long _framesSent;
    private long _keepAlivesSent;
    private long _bytesSent;
    private long _connections;
    private long _writeFailures;
    private long _lastWriteTicks;

    public SerialOutputController(ISerialPortFactory factory, ILogger logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current connection state. Safe to read from any thread.</summary>
    public SerialOutputStatus Status => (SerialOutputStatus)Volatile.Read(ref _status);

    /// <summary>True while a port is open.</summary>
    public bool IsConnected => _port is not null;

    /// <summary>
    /// Counters. Safe to read from any thread: each field is written with Interlocked/Volatile by the owning
    /// thread, so no allocation is needed to publish them.
    /// </summary>
    public SerialOutputStatistics Statistics => new(
        Status,
        Volatile.Read(ref _portName),
        Interlocked.Read(ref _framesSent),
        Interlocked.Read(ref _keepAlivesSent),
        Interlocked.Read(ref _bytesSent),
        Interlocked.Read(ref _connections),
        Interlocked.Read(ref _writeFailures),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastWriteTicks)));

    /// <summary>
    /// Write timeout for a port at <paramref name="baudRate"/>: twice the wire time of the largest possible
    /// frame plus 100 ms, at least <see cref="MinimumWriteTimeout"/>. Long enough never to cut off a healthy
    /// transfer, short enough that a stalled device is detected within a fraction of a second.
    /// </summary>
    public static TimeSpan WriteTimeoutFor(int baudRate)
    {
        var frameBits = (double)AdalightEncoder.FrameLength(LedLayoutSettings.MaxLedCount) * SerialSettings.BitsPerByte8N1;
        var timeout = TimeSpan.FromSeconds(2 * frameBits / baudRate) + TimeSpan.FromMilliseconds(100);
        return timeout > MinimumWriteTimeout ? timeout : MinimumWriteTimeout;
    }

    /// <summary>Takes a new frame (R,G,B per LED, wire order) and encodes it for the next send. Allocation-free.</summary>
    public void SubmitFrame(ReadOnlySpan<byte> ledBytes)
    {
        if (ledBytes.IsEmpty)
        {
            return;
        }

        _frameLength = AdalightEncoder.Encode(ledBytes, _frame);
        _ledCount = ledBytes.Length / 3;
        _pending = true;
    }

    /// <summary>
    /// Advances the state machine to <paramref name="nowTicks"/> (Stopwatch ticks): connects, writes a due
    /// frame or keep-alive, or schedules a retry. Returns how long the caller may sleep before the next step
    /// if no new frame arrives in the meantime.
    /// </summary>
    public TimeSpan Step(long nowTicks, SerialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.PortName))
        {
            ClosePort();
            SetStatus(SerialOutputStatus.Disabled);
            return IdleWait;
        }

        if (_port is not null &&
            (!string.Equals(_portName, settings.PortName, StringComparison.OrdinalIgnoreCase) || _baudRate != settings.BaudRate))
        {
            // Port or baud rate changed in the settings: reopen right away.
            ClosePort();
            _retryAtTicks = nowTicks;
            _retryDelay = InitialRetryDelay;
            SetStatus(SerialOutputStatus.Connecting);
        }

        if (_port is null)
        {
            if (nowTicks < _retryAtTicks)
            {
                return Stopwatch.GetElapsedTime(nowTicks, _retryAtTicks);
            }

            if (!TryOpen(nowTicks, settings))
            {
                return Stopwatch.GetElapsedTime(nowTicks, _retryAtTicks);
            }
        }

        if (_frameLength == 0)
        {
            return IdleWait;
        }

        var keepAliveTicks = settings.KeepAliveMs * Stopwatch.Frequency / 1000;
        var frameIntervalTicks = Stopwatch.Frequency / Math.Max(1, settings.MaxRefreshHz);
        var due = _pending
            ? Math.Min(_lastSentTicks + frameIntervalTicks, _lastSentTicks + keepAliveTicks)
            : _lastSentTicks + keepAliveTicks;

        if (nowTicks >= due)
        {
            if (!Send(nowTicks))
            {
                return Stopwatch.GetElapsedTime(nowTicks, _retryAtTicks);
            }

            due = _lastSentTicks + keepAliveTicks;
        }

        return Stopwatch.GetElapsedTime(nowTicks, due);
    }

    /// <summary>
    /// Ends the session: optionally writes an all-black frame (so the strip goes dark at once instead of
    /// waiting for the firmware watchdog), then closes the port. Never throws.
    /// </summary>
    public void Shutdown(bool blackout)
    {
        if (_port is not null && blackout && _ledCount > 0)
        {
            try
            {
                var length = AdalightEncoder.EncodeBlack(_ledCount, _frame);
                _port.Write(_frame.AsSpan(0, length));
                _frameLength = 0;
            }
            catch (SerialPortException exception)
            {
                SerialLog.BlackoutFailed(_logger, exception);
            }
        }

        ClosePort();
        SetStatus(SerialOutputStatus.Stopped);
    }

    public void Dispose() => ClosePort();

    private bool TryOpen(long nowTicks, SerialSettings settings)
    {
        try
        {
            _port = _factory.Open(settings.PortName, settings.BaudRate, WriteTimeoutFor(settings.BaudRate));
        }
        catch (SerialPortException exception)
        {
            var status = exception.Error switch
            {
                SerialPortError.NotFound => SerialOutputStatus.PortNotFound,
                SerialPortError.Busy => SerialOutputStatus.PortBusy,
                SerialPortError.InvalidConfiguration => SerialOutputStatus.InvalidConfiguration,
                _ => SerialOutputStatus.Reconnecting,
            };

            Volatile.Write(ref _portName, settings.PortName);

            // Logged on the transition only: an unplugged device is polled every second without log spam.
            if (status != Status)
            {
                SerialLog.OpenFailed(_logger, settings.PortName, status, exception.Message);
            }

            SetStatus(status);
            ScheduleRetry(nowTicks, status == SerialOutputStatus.InvalidConfiguration ? InvalidConfigurationRetryDelay : null);
            return false;
        }

        Volatile.Write(ref _portName, settings.PortName);
        _baudRate = settings.BaudRate;
        _retryDelay = InitialRetryDelay;
        Interlocked.Increment(ref _connections);

        // Whatever was last shown goes out immediately on (re)connect.
        _lastSentTicks = 0;
        _pending = _frameLength > 0;

        SerialLog.Connected(_logger, settings.PortName, settings.BaudRate);
        SetStatus(SerialOutputStatus.Connected);
        return true;
    }

    private bool Send(long nowTicks)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            _port!.Write(_frame.AsSpan(0, _frameLength));
        }
        catch (SerialPortException exception)
        {
            Interlocked.Increment(ref _writeFailures);
            SerialLog.Disconnected(_logger, _portName ?? string.Empty, exception.Error, exception.Message);
            ClosePort();
            _retryDelay = InitialRetryDelay;
            SetStatus(SerialOutputStatus.Reconnecting);
            ScheduleRetry(nowTicks, null);
            return false;
        }

        Interlocked.Exchange(ref _lastWriteTicks, Stopwatch.GetTimestamp() - started);
        _lastSentTicks = nowTicks;
        Interlocked.Add(ref _bytesSent, _frameLength);
        if (_pending)
        {
            Interlocked.Increment(ref _framesSent);
            _pending = false;
        }
        else
        {
            Interlocked.Increment(ref _keepAlivesSent);
        }

        return true;
    }

    private void ScheduleRetry(long nowTicks, TimeSpan? fixedDelay)
    {
        var delay = fixedDelay ?? _retryDelay;
        _retryAtTicks = nowTicks + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        if (fixedDelay is null)
        {
            var doubled = _retryDelay * 2;
            _retryDelay = doubled < MaxRetryDelay ? doubled : MaxRetryDelay;
        }
    }

    private void ClosePort()
    {
        var port = _port;
        if (port is null)
        {
            return;
        }

        _port = null;
        try
        {
            port.Dispose();
        }
        catch (IOException exception)
        {
            // A vanished device can make closing fail; the handle is released either way.
            SerialLog.CloseFailed(_logger, exception);
        }
    }

    private void SetStatus(SerialOutputStatus status) => Volatile.Write(ref _status, (int)status);
}
