namespace AmbientLight.Core.Threading;

/// <summary>
/// Wait-free single-producer / single-consumer triple buffer with "latest value wins" semantics.
/// </summary>
/// <remarks>
/// <para>
/// Three pre-allocated slots rotate between three roles: the producer's <i>write</i> slot, a shared
/// <i>middle</i> slot, and the consumer's <i>read</i> slot. Publishing swaps the write slot into the middle
/// with one <see cref="Interlocked.Exchange(ref int, int)"/>; acquiring swaps the middle into the read
/// slot the same way. Neither side ever waits for, spins on, or retries against the other, so:
/// </para>
/// <list type="bullet">
/// <item>the producer (for example the capture thread) can never be blocked by a slow consumer;</item>
/// <item>the consumer always sees the most recent complete frame and never a torn one;</item>
/// <item>frames the consumer did not get to are dropped, which is the desired behavior for real-time
/// output (showing an old color late is worse than skipping it).</item>
/// </list>
/// <para>
/// The full fence implied by <see cref="Interlocked.Exchange(ref int, int)"/> orders the producer's writes
/// into the slot before the hand-off and the consumer's reads after it.
/// </para>
/// <para>
/// Thread-safety contract: exactly one thread calls <see cref="WriteSlot"/>/<see cref="Publish"/> and exactly
/// one (other) thread calls <see cref="ReadSlot"/>/<see cref="TryAcquireLatest"/>. Use
/// <see cref="LatestValueBroadcaster{T}"/> for more than one consumer.
/// </para>
/// </remarks>
/// <typeparam name="T">Slot type; a mutable, pre-allocated buffer such as <see cref="Frames.FrameData"/>.</typeparam>
public sealed class TripleBuffer<T>
    where T : class
{
    private const int IndexMask = 0b011;
    private const int FreshFlag = 0b100;

    private readonly T[] _slots;

    // Index of the middle slot, OR-ed with FreshFlag when it holds a publish the consumer has not taken.
    private int _middle;

    // Owned exclusively by the producer.
    private int _writeIndex;
    private long _publishedCount;

    // Owned exclusively by the consumer.
    private int _readIndex;
    private long _acquiredCount;

    /// <summary>Creates the buffer, calling <paramref name="slotFactory"/> three times.</summary>
    public TripleBuffer(Func<T> slotFactory)
    {
        ArgumentNullException.ThrowIfNull(slotFactory);
        _slots = [CreateSlot(slotFactory), CreateSlot(slotFactory), CreateSlot(slotFactory)];
        _writeIndex = 0;
        _middle = 1;
        _readIndex = 2;
    }

    /// <summary>Producer side: the slot to fill before calling <see cref="Publish"/>.</summary>
    public T WriteSlot => _slots[_writeIndex];

    /// <summary>
    /// Producer side: hands the filled write slot to the consumer and takes a new write slot.
    /// Wait-free: one atomic exchange, never blocks.
    /// </summary>
    public void Publish()
    {
        var previous = Interlocked.Exchange(ref _middle, _writeIndex | FreshFlag);
        _writeIndex = previous & IndexMask;
        Volatile.Write(ref _publishedCount, _publishedCount + 1);
    }

    /// <summary>Consumer side: the slot most recently acquired (initially an empty slot).</summary>
    public T ReadSlot => _slots[_readIndex];

    /// <summary>
    /// Consumer side: if a publish happened since the last acquire, makes the newest value the read slot
    /// and returns <see langword="true"/>. Otherwise leaves the read slot unchanged and returns
    /// <see langword="false"/>. Wait-free.
    /// </summary>
    public bool TryAcquireLatest()
    {
        if ((Volatile.Read(ref _middle) & FreshFlag) == 0)
        {
            return false;
        }

        // Only the consumer clears FreshFlag, so the middle slot is still fresh here even if the
        // producer published again in between; the exchange simply picks up the newer value.
        var previous = Interlocked.Exchange(ref _middle, _readIndex);
        _readIndex = previous & IndexMask;
        Volatile.Write(ref _acquiredCount, _acquiredCount + 1);
        return true;
    }

    /// <summary>Total publishes so far. Safe to read from any thread (diagnostics).</summary>
    public long PublishedCount => Volatile.Read(ref _publishedCount);

    /// <summary>Total successful acquires so far. Safe to read from any thread (diagnostics).</summary>
    public long AcquiredCount => Volatile.Read(ref _acquiredCount);

    /// <summary>
    /// Publishes the consumer never saw because a newer one replaced them first (diagnostics).
    /// Approximate while both sides are running; exact when they are idle.
    /// </summary>
    public long DroppedCount
    {
        get
        {
            var acquired = AcquiredCount;
            var published = PublishedCount;
            var pending = (Volatile.Read(ref _middle) & FreshFlag) != 0 ? 1 : 0;
            return Math.Max(0, published - acquired - pending);
        }
    }

    private static T CreateSlot(Func<T> slotFactory) =>
        slotFactory() ?? throw new InvalidOperationException("The slot factory returned null.");
}
