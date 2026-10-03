using AmbientLight.Core.Frames;
using AmbientLight.Core.Threading;

namespace AmbientLight.Core.Tests;

public sealed class TripleBufferTests
{
    private sealed class Payload
    {
        public long First { get; set; }

        public long Second { get; set; }

        public long[] Body { get; } = new long[64];
    }

    [Fact]
    public void TryAcquireLatest_ReturnsFalse_BeforeAnyPublish()
    {
        var buffer = new TripleBuffer<Payload>(() => new Payload());

        Assert.False(buffer.TryAcquireLatest());
        Assert.Equal(0, buffer.ReadSlot.First);
    }

    [Fact]
    public void Consumer_SeesOnlyTheNewestPublish()
    {
        var buffer = new TripleBuffer<Payload>(() => new Payload());

        for (var value = 1; value <= 5; value++)
        {
            buffer.WriteSlot.First = value;
            buffer.Publish();
        }

        Assert.True(buffer.TryAcquireLatest());
        Assert.Equal(5, buffer.ReadSlot.First);
        Assert.False(buffer.TryAcquireLatest());
        Assert.Equal(5, buffer.ReadSlot.First);
        Assert.Equal(5, buffer.PublishedCount);
        Assert.Equal(1, buffer.AcquiredCount);
        Assert.Equal(4, buffer.DroppedCount);
    }

    [Fact]
    public void Slots_AreNeverShared_BetweenWriterAndReader()
    {
        var buffer = new TripleBuffer<Payload>(() => new Payload());

        for (var i = 0; i < 100; i++)
        {
            buffer.WriteSlot.First = i;
            buffer.Publish();
            if (i % 3 == 0)
            {
                buffer.TryAcquireLatest();
            }

            Assert.NotSame(buffer.WriteSlot, buffer.ReadSlot);
        }
    }

    [Fact]
    public async Task ConcurrentProducerAndConsumer_NeverObserveTornOrStaleFrames()
    {
        const long frames = 2_000_000;
        var buffer = new TripleBuffer<Payload>(() => new Payload());
        var cancellation = TestContext.Current.CancellationToken;

        var producer = Task.Run(
            () =>
            {
                for (long sequence = 1; sequence <= frames; sequence++)
                {
                    var slot = buffer.WriteSlot;
                    slot.First = sequence;
                    Array.Fill(slot.Body, sequence);
                    slot.Second = sequence;
                    buffer.Publish();
                }
            },
            cancellation);

        var consumer = Task.Run(
            () =>
            {
                long last = 0;
                long observed = 0;
                while (last < frames)
                {
                    if (!buffer.TryAcquireLatest())
                    {
                        Thread.SpinWait(8);
                        continue;
                    }

                    var slot = buffer.ReadSlot;
                    Assert.Equal(slot.First, slot.Second);
                    Assert.All(slot.Body, value => Assert.Equal(slot.First, value));
                    Assert.True(slot.First > last, $"Sequence went backwards: {slot.First} after {last}.");
                    last = slot.First;
                    observed++;
                }

                return observed;
            },
            cancellation);

        await producer;
        var observedFrames = await consumer;

        Assert.Equal(frames, buffer.PublishedCount);
        Assert.Equal(observedFrames, buffer.AcquiredCount);
        Assert.Equal(frames - observedFrames, buffer.DroppedCount);
    }

    [Fact]
    public void Mailbox_WaitTimesOut_WhenNothingPublished()
    {
        using var mailbox = new LatestValueMailbox<Payload>(() => new Payload());

        Assert.False(mailbox.WaitAndAcquireLatest(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task Mailbox_WakesWaitingConsumer_OnPublish()
    {
        using var mailbox = new LatestValueMailbox<Payload>(() => new Payload());
        var cancellation = TestContext.Current.CancellationToken;

        var consumer = Task.Run(() => mailbox.WaitAndAcquireLatest(TimeSpan.FromSeconds(10)), cancellation);
        await Task.Delay(50, cancellation);
        mailbox.WriteSlot.First = 42;
        mailbox.Publish();

        Assert.True(await consumer);
        Assert.Equal(42, mailbox.ReadSlot.First);
    }

    [Fact]
    public void Broadcaster_GivesEverySubscriberAnIndependentCopy()
    {
        using var broadcaster = new LatestValueBroadcaster<FrameData>(2, () => new FrameData(8));
        var source = new FrameData(8) { Sequence = 7 };
        source.SetZoneCount(2);
        source.Colors[0] = new Color.ColorRgb(1, 2, 3);
        source.Colors[1] = new Color.ColorRgb(4, 5, 6);

        broadcaster.Publish(source);
        source.Colors[0] = Color.ColorRgb.White;

        for (var i = 0; i < broadcaster.SubscriberCount; i++)
        {
            var mailbox = broadcaster.GetSubscriber(i);
            Assert.True(mailbox.TryAcquireLatest());
            Assert.Equal(7, mailbox.ReadSlot.Sequence);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, mailbox.ReadSlot.ColorBytes.ToArray());
        }

        Assert.NotSame(broadcaster.GetSubscriber(0).ReadSlot, broadcaster.GetSubscriber(1).ReadSlot);
    }
}
