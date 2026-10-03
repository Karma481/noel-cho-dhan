using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(new AppSettings().Validate());
    }

    [Fact]
    public void Json_RoundTrips_WithEnumsAsStrings()
    {
        var original = new AppSettings
        {
            Serial = new SerialSettings { Enabled = true, PortName = "COM7", BaudRate = 2_000_000 },
            LedLayout = new LedLayoutSettings { StartCorner = StripStartCorner.TopRight, Direction = StripDirection.CounterClockwise },
            Processing = new ProcessingSettings { WhiteBalance = new RgbGain(1f, 0.9f, 0.8f) },
        };

        var json = AppSettingsStore.Serialize(original);
        var restored = AppSettingsStore.Deserialize(json);

        Assert.Contains("\"counterClockwise\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totalLedCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Deserialize_KeepsDefaults_ForMissingProperties()
    {
        var settings = AppSettingsStore.Deserialize("""{ "serial": { "portName": "COM4" } }""");

        Assert.Equal("COM4", settings.Serial.PortName);
        Assert.Equal(1_000_000, settings.Serial.BaudRate);
        Assert.Equal(new LedLayoutSettings(), settings.LedLayout);
    }

    [Fact]
    public void Deserialize_EmptyObject_EqualsDefaults()
    {
        Assert.Equal(new AppSettings(), AppSettingsStore.Deserialize("{}"));
    }

    [Fact]
    public void Deserialize_NullSection_KeepsDefaultSection_AndAcceptsComments()
    {
        var settings = AppSettingsStore.Deserialize("""
            {
              // A section explicitly set to null falls back to its defaults.
              "overlay": null,
              "processing": { "whiteBalance": { "b": 0.5 } },
            }
            """);

        Assert.Equal(new OverlaySettings(), settings.Overlay);
        Assert.Equal(new RgbGain(1f, 1f, 0.5f), settings.Processing.WhiteBalance);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Deserialize_Throws_InvalidData_OnMalformedJson(string json)
    {
        Assert.Throws<InvalidDataException>(() => AppSettingsStore.Deserialize(json));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsThroughDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ambientlight-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings { Overlay = new OverlaySettings { Opacity = 0.5f } };
        var cancellation = TestContext.Current.CancellationToken;

        try
        {
            await AppSettingsStore.SaveAsync(path, settings, cancellation);
            var loaded = await AppSettingsStore.LoadAsync(path, cancellation);

            Assert.Equal(0.5f, loaded.Overlay.Opacity);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_ReturnsDefaults_WhenFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "ambientlight-missing-" + Guid.NewGuid().ToString("N") + ".json");

        var loaded = await AppSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(new LedLayoutSettings(), loaded.LedLayout);
    }

    [Fact]
    public void Validate_ReportsOutOfRangeValues_ByPath()
    {
        var settings = new AppSettings
        {
            Capture = new CaptureSettings { MaxFps = 0 },
            Processing = new ProcessingSettings { Brightness = float.NaN },
            Serial = new SerialSettings { Enabled = true, PortName = " " },
            LedLayout = new LedLayoutSettings { TopCount = 0, RightCount = 0, BottomCount = 0, LeftCount = 0 },
        };

        var paths = settings.Validate()
            .Where(issue => issue.Severity == SettingsIssueSeverity.Error)
            .Select(issue => issue.Path)
            .ToHashSet();

        Assert.Contains("capture.maxFps", paths);
        Assert.Contains("processing.brightness", paths);
        Assert.Contains("serial.portName", paths);
        Assert.Contains("ledLayout", paths);
    }

    [Fact]
    public void Validate_WarnsWhenTheSerialLinkCannotKeepUp()
    {
        var settings = new AppSettings
        {
            Serial = new SerialSettings { Enabled = true, PortName = "COM3", BaudRate = 115_200, MaxRefreshHz = 60 },
        };

        var issues = settings.Validate();

        var issue = Assert.Single(issues);
        Assert.Equal(SettingsIssueSeverity.Warning, issue.Severity);
        Assert.Equal("serial.baudRate", issue.Path);
    }

    [Fact]
    public void EstimateTransmitTime_MatchesAdalightFrameSize()
    {
        var serial = new SerialSettings { BaudRate = 1_000_000 };

        // 300 LEDs: 6-byte header + 900 payload bytes, 10 bits per byte at 1 Mbaud = 9.06 ms.
        Assert.Equal(9.06, serial.EstimateTransmitTime(300).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void SettingsHolder_PublishesNewVersion_AndRejectsInvalidSettings()
    {
        var holder = new SettingsHolder(new AppSettings());
        var first = holder.Current;

        var accepted = holder.TryPublish(
            new AppSettings { LedLayout = new LedLayoutSettings { TopCount = 10 } },
            out _);
        var rejected = holder.TryPublish(
            new AppSettings { Capture = new CaptureSettings { MaxFps = -1 } },
            out var issues);

        Assert.True(accepted);
        Assert.False(rejected);
        Assert.NotEmpty(issues);
        Assert.Equal(first.Version + 1, holder.Current.Version);
        Assert.Equal(10 + 18 + 32 + 18, holder.Current.Zones.Length);
    }

    [Fact]
    public void FrameData_CopyFrom_CopiesColorsAndMetadata()
    {
        var source = new FrameData(4) { Sequence = 3, LayoutVersion = 2, SourceWidth = 3840, SourceHeight = 2160, IsTransitioning = true };
        source.SetZoneCount(2);
        source.Colors[1] = Color.ColorRgb.White;
        var target = new FrameData(4);

        target.CopyFrom(source);

        Assert.Equal(2, target.ZoneCount);
        Assert.Equal(Color.ColorRgb.White, target.Colors[1]);
        Assert.Equal(3, target.Sequence);
        Assert.Equal(2, target.LayoutVersion);
        Assert.Equal(3840, target.SourceWidth);
        Assert.True(target.IsTransitioning);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameData(1).CopyFrom(source));
    }
}
