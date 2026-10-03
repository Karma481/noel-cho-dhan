using System.Diagnostics;
using System.Numerics;
using AmbientLight.Core.Color;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Core.Zones;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmbientLight.Processing.Tests;

public sealed class ColorProcessingServiceTests
{
    [Fact]
    public void EndToEnd_CaptureFrameFlowsToEveryOutput_AndLetterboxFeedsBack()
    {
        var holder = new SettingsHolder(new AppSettings
        {
            Processing = new ProcessingSettings { SmoothingTimeMs = 0, Saturation = 1f },
            Letterbox = new LetterboxSettings { StableTimeMs = 0 },
        });
        using var input = new LatestValueMailbox<ZoneSampleFrame>(() => new ZoneSampleFrame(LedLayoutSettings.MaxLedCount));
        using var output = new LatestValueBroadcaster<FrameData>(2, () => new FrameData(LedLayoutSettings.MaxLedCount));
        var contentBounds = new SnapshotCell<NormalizedRect>(NormalizedRect.Full);
        using var service = new ColorProcessingService(holder, input, output, contentBounds, NullLogger<ColorProcessingService>.Instance);

        service.Start();
        Assert.True(service.IsRunning);

        // Two captures of a letterboxed white picture (two, so the stability window can elapse).
        for (var i = 1; i <= 2; i++)
        {
            var frame = input.WriteSlot;
            frame.SetZoneCount(holder.Current.Zones.Length);
            frame.Samples.Fill(Vector3.One);
            frame.LayoutVersion = holder.Current.Version;
            frame.Sequence = i;
            frame.HasProfile = true;
            frame.RowLuma.Fill(1f);
            frame.ColumnLuma.Fill(1f);
            frame.RowLuma[..33].Clear();
            frame.RowLuma[^33..].Clear();
            var now = Stopwatch.GetTimestamp();
            frame.Timing = new FrameTiming(now, now, 0);
            input.Publish();

            for (var subscriber = 0; subscriber < output.SubscriberCount; subscriber++)
            {
                var mailbox = output.GetSubscriber(subscriber);
                Assert.True(WaitForSequence(mailbox, i), $"Subscriber {subscriber} never received frame {i}.");
                Assert.Equal(holder.Current.Zones.Length, mailbox.ReadSlot.ZoneCount);
                Assert.Equal(ColorRgb.White, mailbox.ReadSlot.DisplayColors[0]);
            }
        }

        Assert.True(SpinWait.SpinUntil(() => contentBounds.Current.Value != NormalizedRect.Full, TimeSpan.FromSeconds(5)));
        Assert.Equal(33f / 256f, contentBounds.Current.Value.Y, precision: 6);

        var statistics = service.GetStatistics();
        Assert.Equal(2, statistics.FramesIngested);
        Assert.True(statistics.FramesPublished >= 2);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Stop_IsIdempotent_AndStartTwiceThrows()
    {
        var holder = new SettingsHolder(new AppSettings());
        using var input = new LatestValueMailbox<ZoneSampleFrame>(() => new ZoneSampleFrame(LedLayoutSettings.MaxLedCount));
        using var output = new LatestValueBroadcaster<FrameData>(1, () => new FrameData(LedLayoutSettings.MaxLedCount));
        using var service = new ColorProcessingService(
            holder, input, output, new SnapshotCell<NormalizedRect>(NormalizedRect.Full), NullLogger<ColorProcessingService>.Instance);

        service.Stop();
        service.Start();
        Assert.Throws<InvalidOperationException>(service.Start);
        service.Stop();
        service.Stop();
    }

    private static bool WaitForSequence(LatestValueMailbox<FrameData> mailbox, long sequence)
    {
        var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            mailbox.WaitAndAcquireLatest(TimeSpan.FromMilliseconds(100));
            if (mailbox.ReadSlot.Sequence >= sequence)
            {
                return true;
            }
        }

        return false;
    }
}
