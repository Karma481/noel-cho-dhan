using AmbientLight.Core.Color;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Overlay.Rendering;

namespace AmbientLight.Overlay.Tests;

public sealed class RedrawTrackerTests
{
    private static readonly OverlaySettings Settings = new();
    private static readonly GlowLayout Layout = GlowGeometry.ComputeLayout(3840, 2160, Settings);

    [Fact]
    public void FirstFrame_IsAlwaysDrawn()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());

        Assert.True(new RedrawTracker().NeedsRedraw(new ColorRgb[zones.Length], Layout, Settings, zones));
    }

    [Fact]
    public void IdenticalColors_AreNotRedrawn()
    {
        var (tracker, zones, colors) = Drawn();

        Assert.False(tracker.NeedsRedraw((ColorRgb[])colors.Clone(), Layout, Settings, zones));
    }

    [Fact]
    public void ASingleChannelChange_TriggersARedraw()
    {
        var (tracker, zones, colors) = Drawn();
        var changed = (ColorRgb[])colors.Clone();
        changed[^1] = changed[^1] with { B = (byte)(changed[^1].B + 1) };

        Assert.True(tracker.NeedsRedraw(changed, Layout, Settings, zones));
    }

    [Fact]
    public void ResizeOrNewMonitor_TriggersARedraw()
    {
        var (tracker, zones, colors) = Drawn();

        Assert.True(tracker.NeedsRedraw(colors, GlowGeometry.ComputeLayout(1920, 1080, Settings), Settings, zones));
    }

    [Fact]
    public void SettingsChange_TriggersARedraw_ButAnEqualCopyDoesNot()
    {
        var (tracker, zones, colors) = Drawn();

        Assert.True(tracker.NeedsRedraw(colors, Layout, Settings with { Opacity = 0.5f }, zones));
        Assert.False(tracker.NeedsRedraw(colors, Layout, Settings with { }, zones));
    }

    [Fact]
    public void NewZoneLayout_TriggersARedraw()
    {
        var (tracker, _, colors) = Drawn();
        var rebuilt = ZoneLayoutBuilder.Build(new LedLayoutSettings());

        Assert.True(tracker.NeedsRedraw(colors, Layout, Settings, rebuilt));
    }

    [Fact]
    public void Invalidate_ForcesARedraw()
    {
        var (tracker, zones, colors) = Drawn();

        tracker.Invalidate();

        Assert.True(tracker.NeedsRedraw(colors, Layout, Settings, zones));
    }

    [Fact]
    public void Checking_AllocatesNothing()
    {
        var (tracker, zones, colors) = Drawn();
        tracker.NeedsRedraw(colors, Layout, Settings, zones);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            tracker.NeedsRedraw(colors, Layout, Settings, zones);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static (RedrawTracker Tracker, System.Collections.Immutable.ImmutableArray<ZoneConfig> Zones, ColorRgb[] Colors) Drawn()
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var colors = Enumerable.Range(0, zones.Length).Select(i => new ColorRgb((byte)i, (byte)(i * 2), (byte)(255 - i))).ToArray();
        var tracker = new RedrawTracker();
        tracker.MarkDrawn(colors, Layout, Settings, zones);
        return (tracker, zones, colors);
    }
}
