using AmbientLight.Core.Frames;

namespace AmbientLight.Core.Threading;

/// <summary>
/// Fans one producer out to several independent consumers, each with its own
/// <see cref="LatestValueMailbox{T}"/>.
/// </summary>
/// <remarks>
/// Every consumer gets a private copy of each value, so a slow consumer (a serial port stalled on a
/// USB bridge) can neither delay the producer nor make another consumer (the overlay) miss frames.
/// The copy is a few hundred bytes per frame, far cheaper than any form of shared-ownership tracking.
/// </remarks>
public sealed class LatestValueBroadcaster<T> : IDisposable
    where T : class, ICopyFrom<T>
{
    private readonly LatestValueMailbox<T>[] _subscribers;
    private readonly int[] _enabled;

    /// <summary>Creates <paramref name="subscriberCount"/> mailboxes whose slots come from <paramref name="slotFactory"/>.</summary>
    public LatestValueBroadcaster(int subscriberCount, Func<T> slotFactory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberCount);
        ArgumentNullException.ThrowIfNull(slotFactory);

        _subscribers = new LatestValueMailbox<T>[subscriberCount];
        _enabled = new int[subscriberCount];
        for (var i = 0; i < subscriberCount; i++)
        {
            _subscribers[i] = new LatestValueMailbox<T>(slotFactory);
            _enabled[i] = 1;
        }
    }

    /// <summary>Number of consumers.</summary>
    public int SubscriberCount => _subscribers.Length;

    /// <summary>The mailbox owned by consumer <paramref name="index"/>; hand each one to exactly one thread.</summary>
    public LatestValueMailbox<T> GetSubscriber(int index) => _subscribers[index];

    /// <summary>
    /// Includes or skips consumer <paramref name="index"/> in future publishes. A consumer whose thread is not
    /// running is disabled so the producer does not copy frames nobody will read. Safe from any thread.
    /// </summary>
    public void SetSubscriberEnabled(int index, bool enabled) => Volatile.Write(ref _enabled[index], enabled ? 1 : 0);

    /// <summary>True when consumer <paramref name="index"/> receives publishes.</summary>
    public bool IsSubscriberEnabled(int index) => Volatile.Read(ref _enabled[index]) != 0;

    /// <summary>Producer side: copies <paramref name="value"/> into every enabled consumer's mailbox and publishes. Never blocks.</summary>
    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var i = 0; i < _subscribers.Length; i++)
        {
            if (Volatile.Read(ref _enabled[i]) == 0)
            {
                continue;
            }

            var subscriber = _subscribers[i];
            subscriber.WriteSlot.CopyFrom(value);
            subscriber.Publish();
        }
    }

    /// <summary>Wakes every consumer, typically during shutdown.</summary>
    public void WakeAll()
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.Wake();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.Dispose();
        }
    }
}
