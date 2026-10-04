using System.Numerics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Threading;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Tests;

public sealed class Phase2ContractTests
{
    [Fact]
    public void SnapshotCell_PublishesOnlyChanges_WithIncreasingVersions()
    {
        var cell = new SnapshotCell<NormalizedRect>(NormalizedRect.Full);
        var initial = cell.Current;

        Assert.False(cell.Publish(NormalizedRect.Full));
        Assert.Same(initial, cell.Current);

        var letterbox = new NormalizedRect(0f, 0.128f, 1f, 0.744f);
        Assert.True(cell.Publish(letterbox));
        Assert.Equal(letterbox, cell.Current.Value);
        Assert.Equal(1, cell.Current.Version);
    }

    [Fact]
    public void SnapshotCell_RepeatedEqualPublishes_AllocateNothing()
    {
        var cell = new SnapshotCell<NormalizedRect>(new NormalizedRect(0f, 0.1f, 1f, 0.8f));
        var value = new NormalizedRect(0f, 0.1f, 1f, 0.8f);
        cell.Publish(value);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            cell.Publish(value);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Within_MapsARectIntoAContainer()
    {
        var content = new NormalizedRect(0.1f, 0.2f, 0.8f, 0.6f);

        Assert.Equal(content, NormalizedRect.Full.Within(content));
        var topStrip = new NormalizedRect(0f, 0f, 1f, 0.1f).Within(content);
        Assert.Equal(0.1f, topStrip.X, precision: 6);
        Assert.Equal(0.2f, topStrip.Y, precision: 6);
        Assert.Equal(0.8f, topStrip.Width, precision: 6);
        Assert.Equal(0.06f, topStrip.Height, precision: 6);
    }

    [Fact]
    public void ZoneSampleFrame_CopiesProfileAndContentBounds()
    {
        var source = new ZoneSampleFrame(4) { HasProfile = true, ContentBounds = new NormalizedRect(0f, 0.1f, 1f, 0.8f) };
        source.SetZoneCount(1);
        source.Samples[0] = Vector3.One;
        source.RowLuma[5] = 0.7f;
        source.ColumnLuma[^1] = 0.3f;
        var target = new ZoneSampleFrame(4);

        target.CopyFrom(source);

        Assert.True(target.HasProfile);
        Assert.Equal(source.ContentBounds, target.ContentBounds);
        Assert.Equal(0.7f, target.RowLuma[5]);
        Assert.Equal(0.3f, target.ColumnLuma[^1]);
        Assert.Equal(ZoneSampleFrame.ProfileResolution * 2, target.LineLuma.Length);
    }

    [Fact]
    public void NewSettings_HaveSafeDefaults_AndAreValidated()
    {
        var defaults = new AppSettings();

        Assert.Equal(400, defaults.Serial.MaxCurrentMilliamps);
        Assert.Equal(6500, defaults.Processing.ColorTemperatureK);
        Assert.True(defaults.Letterbox.Enabled);

        var invalid = new AppSettings
        {
            Processing = new ProcessingSettings { ColorTemperatureK = 1000 },
            Letterbox = new LetterboxSettings { MaxBarFraction = 0.9f },
        };
        var paths = invalid.Validate().Select(issue => issue.Path).ToHashSet();
        Assert.Contains("processing.colorTemperatureK", paths);
        Assert.Contains("letterbox.maxBarFraction", paths);
    }

    [Fact]
    public void Validate_WarnsWhenIdleCurrentAloneExceedsTheBudget()
    {
        var settings = new AppSettings
        {
            Serial = new SerialSettings { Enabled = true, PortName = "COM3", BaudRate = 2_000_000, MaxCurrentMilliamps = 50 },
        };

        var issue = Assert.Single(settings.Validate());
        Assert.Equal("serial.maxCurrentMilliamps", issue.Path);
        Assert.Equal(SettingsIssueSeverity.Warning, issue.Severity);
    }
}

public sealed class Phase3ContractTests
{
    [Fact]
    public void OverlaySettings_DefaultsAreValid_AndNewParametersAreRangeChecked()
    {
        Assert.Empty(new AppSettings().Validate());

        var invalid = new AppSettings
        {
            Overlay = new OverlaySettings { SpreadFraction = 0f, BlurRadiusFraction = 0.6f, Brightness = 2f, Opacity = -0.1f },
        };
        var paths = invalid.Validate().Select(issue => issue.Path).ToHashSet();

        Assert.Contains("overlay.spreadFraction", paths);
        Assert.Contains("overlay.blurRadiusFraction", paths);
        Assert.Contains("overlay.brightness", paths);
        Assert.Contains("overlay.opacity", paths);
    }

    [Fact]
    public void Mailbox_AvailableWaitHandle_IsSignalledByPublish_AndResetByTheWait()
    {
        using var mailbox = new LatestValueMailbox<FrameData>(() => new FrameData(4));

        Assert.False(mailbox.AvailableWaitHandle.WaitOne(0));
        mailbox.Publish();
        Assert.True(mailbox.AvailableWaitHandle.WaitOne(0));
        Assert.False(mailbox.AvailableWaitHandle.WaitOne(0));
        Assert.True(mailbox.TryAcquireLatest());
    }
}
