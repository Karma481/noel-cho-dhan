namespace AmbientLight.Core.Threading;

/// <summary>
/// A <see cref="TripleBuffer{T}"/> plus a wake-up signal, so a dedicated consumer thread can sleep until
/// a new value arrives instead of polling.
/// </summary>
/// <remarks>
/// The data path stays lock-free: the signal carries no data and setting it never blocks the producer.
/// It only lets the consumer park in the kernel (zero CPU) while there is nothing to do.
/// Same single-producer / single-consumer contract as <see cref="TripleBuffer{T}"/>.
/// </remarks>
public sealed class LatestValueMailbox<T> : IDisposable
    where T : class
{
    private readonly TripleBuffer<T> _buffer;
    private readonly AutoResetEvent _signal = new(initialState: false);

    /// <summary>Creates the mailbox, calling <paramref name="slotFactory"/> three times.</summary>
    public LatestValueMailbox(Func<T> slotFactory)
    {
        _buffer = new TripleBuffer<T>(slotFactory);
    }

    /// <summary>Producer side: the slot to fill before calling <see cref="Publish"/>.</summary>
    public T WriteSlot => _buffer.WriteSlot;

    /// <summary>Producer side: publishes the write slot and wakes the consumer. Never blocks.</summary>
    public void Publish()
    {
        _buffer.Publish();
        _signal.Set();
    }

    /// <summary>Consumer side: the most recently acquired value.</summary>
    public T ReadSlot => _buffer.ReadSlot;

    /// <summary>Consumer side: takes the newest value if there is one, without waiting.</summary>
    public bool TryAcquireLatest() => _buffer.TryAcquireLatest();

    /// <summary>
    /// Consumer side: takes the newest value, waiting up to <paramref name="timeout"/> for one to be published.
    /// Returns <see langword="false"/> on timeout or when woken by <see cref="Wake"/> with nothing new;
    /// callers check their cancellation token and loop.
    /// </summary>
    public bool WaitAndAcquireLatest(TimeSpan timeout)
    {
        if (_buffer.TryAcquireLatest())
        {
            return true;
        }

        _signal.WaitOne(timeout);
        return _buffer.TryAcquireLatest();
    }

    /// <summary>
    /// The auto-reset event signalled by <see cref="Publish"/> and <see cref="Wake"/>, for consumers that must
    /// wait on it together with other handles, such as a Win32 message loop using
    /// <c>MsgWaitForMultipleObjectsEx</c>. A satisfied wait resets it; follow it with <see cref="TryAcquireLatest"/>.
    /// </summary>
    public WaitHandle AvailableWaitHandle => _signal;

    /// <summary>Wakes a waiting consumer without publishing, for example to observe shutdown promptly.</summary>
    public void Wake() => _signal.Set();

    /// <summary>Total publishes so far (diagnostics).</summary>
    public long PublishedCount => _buffer.PublishedCount;

    /// <summary>Publishes the consumer skipped because newer ones arrived first (diagnostics).</summary>
    public long DroppedCount => _buffer.DroppedCount;

    /// <inheritdoc />
    public void Dispose() => _signal.Dispose();
}
