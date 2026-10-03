using System.Diagnostics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Serial.Ports;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Serial;

/// <summary>
/// Pipeline stage T4: sends <see cref="FrameData.LedBytes"/> to the LED controller on its own thread.
/// </summary>
/// <remarks>
/// <para>
/// The thread sleeps on its input mailbox and wakes for a new frame or when the controller's next deadline
/// (rate limit, keep-alive, reconnect retry) is due. Writes are synchronous on this thread only, bounded by
/// the port's write timeout; a slow or stalled device therefore never delays capture, processing or the
/// overlay, which hand frames over through wait-free mailboxes and simply drop what this stage has not
/// consumed yet.
/// </para>
/// <para>All decisions live in <see cref="SerialOutputController"/>; this class only supplies time and frames.</para>
/// </remarks>
public sealed class SerialOutputService : IDisposable
{
    private const string ThreadName = "AmbientLight.Serial";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsHolder _settings;
    private readonly LatestValueMailbox<FrameData> _input;
    private readonly SerialOutputController _controller;
    private readonly ILogger<SerialOutputService> _logger;
    private readonly Lock _lifecycleLock = new();

    private Thread? _thread;
    private volatile bool _stopRequested;

    /// <summary>Creates the service with real Win32 COM ports.</summary>
    /// <param name="settings">Configuration source.</param>
    /// <param name="input">Mailbox of the processing stage's broadcaster reserved for serial output; this service is its only consumer.</param>
    /// <param name="logger">Diagnostics sink.</param>
    public SerialOutputService(SettingsHolder settings, LatestValueMailbox<FrameData> input, ILogger<SerialOutputService> logger)
        : this(settings, input, new Win32SerialPortFactory(), logger)
    {
    }

    /// <summary>Creates the service with a custom port factory (tests, simulators).</summary>
    public SerialOutputService(
        SettingsHolder settings,
        LatestValueMailbox<FrameData> input,
        ISerialPortFactory portFactory,
        ILogger<SerialOutputService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _controller = new SerialOutputController(portFactory ?? throw new ArgumentNullException(nameof(portFactory)), logger);
    }

    /// <summary>Latest counters and connection state. Safe to call from any thread.</summary>
    public SerialOutputStatistics GetStatistics() => _controller.Statistics;

    /// <summary>Starts the output thread.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Serial output is already running.");
            }

            _stopRequested = false;
            _thread = new Thread(RunOutputLoop)
            {
                Name = ThreadName,
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the output thread, sending a blackout frame first when <see cref="SerialSettings.BlackoutOnStop"/>
    /// is set, and closes the port. Safe to call repeatedly.
    /// </summary>
    /// <exception cref="TimeoutException">The thread did not exit in time.</exception>
    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            thread = _thread;
            if (thread is null)
            {
                return;
            }

            _stopRequested = true;
            _input.Wake();
        }

        if (!thread.Join(StopTimeout))
        {
            throw new TimeoutException($"The serial output thread did not stop within {StopTimeout.TotalSeconds} s.");
        }

        lock (_lifecycleLock)
        {
            _thread = null;
        }
    }

    /// <summary>Port names present on this machine, for the settings UI.</summary>
    public static IReadOnlyList<string> GetAvailablePorts() => new Win32SerialPortFactory().GetPortNames();

    /// <summary>Stops the output; a thread that does not stop in time is abandoned (it is a background thread).</summary>
    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (TimeoutException)
        {
            // A write is bounded by the port's write timeout, so this only happens if the driver itself hangs.
            return;
        }

        _controller.Dispose();
    }

    private void RunOutputLoop()
    {
        SerialLog.Started(_logger);
        var wait = TimeSpan.Zero;
        try
        {
            while (!_stopRequested)
            {
                if (_input.WaitAndAcquireLatest(wait))
                {
                    _controller.SubmitFrame(_input.ReadSlot.LedBytes);
                }

                if (_stopRequested)
                {
                    break;
                }

                var next = _controller.Step(Stopwatch.GetTimestamp(), _settings.Current.Settings.Serial);

                // Re-check settings at least this often even if no frame and no deadline arrives.
                wait = next < SerialOutputController.IdleWait ? next : SerialOutputController.IdleWait;
            }
        }
        finally
        {
            _controller.Shutdown(_settings.Current.Settings.Serial.BlackoutOnStop);
            SerialLog.Stopped(_logger);
        }
    }
}
