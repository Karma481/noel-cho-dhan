using AmbientLight.Core.Settings;
using AmbientLight.Host.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AmbientLight.Host.Tests;

public sealed class SettingsCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AmbientLight.Tests." + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero));

    public SettingsCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FirstRun_WritesTheDefaults_WithTheLedStripOff()
    {
        using var coordinator = CreateCoordinator();

        var result = coordinator.Load();

        Assert.Equal(SettingsLoadOutcome.CreatedDefaults, result.Outcome);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.True(File.Exists(ConfigPath));

        var written = AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath));
        Assert.False(written.Serial.Enabled);
        Assert.True(written.Overlay.Enabled);
        Assert.Contains("\"enabled\": false", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidFile_IsLoadedAsIs()
    {
        var custom = new AppSettings { Capture = new CaptureSettings { MaxFps = 30 } };
        AppSettingsStore.Save(ConfigPath, custom);
        using var coordinator = CreateCoordinator();

        var result = coordinator.Load();

        Assert.Equal(SettingsLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(custom, result.Settings);
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public void MalformedFile_IsBackedUpAndReplacedWithDefaults()
    {
        const string Broken = "{ \"overlay\": { \"opacity\": 0.5, ";
        File.WriteAllText(ConfigPath, Broken);
        using var coordinator = CreateCoordinator();

        var result = coordinator.Load();

        Assert.Equal(SettingsLoadOutcome.ResetToDefaults, result.Outcome);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("config.invalid-20261003-093000.json", Path.GetFileName(result.BackupPath));
        Assert.Equal(Broken, File.ReadAllText(result.BackupPath));
        Assert.Equal(new AppSettings(), AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath)));
    }

    [Fact]
    public void InvalidSection_IsReset_AndValidSectionsAreKept()
    {
        File.WriteAllText(ConfigPath, """
            {
              "capture": { "maxFps": 30 },
              "overlay": { "opacity": 5.0, "brightness": 0.5 }
            }
            """);
        using var coordinator = CreateCoordinator();

        var result = coordinator.Load();

        Assert.Equal(SettingsLoadOutcome.Repaired, result.Outcome);
        Assert.Equal(30, result.Settings.Capture.MaxFps);
        Assert.Equal(new OverlaySettings(), result.Settings.Overlay);
        Assert.Contains(result.Issues, issue => issue.Path == "overlay.opacity");
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(result.Settings, AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath)));
    }

    [Fact]
    public void LedStripEnabledWithoutPort_IsRepairedToOff()
    {
        File.WriteAllText(ConfigPath, """{ "serial": { "enabled": true, "portName": "" } }""");
        using var coordinator = CreateCoordinator();

        var result = coordinator.Load();

        Assert.Equal(SettingsLoadOutcome.Repaired, result.Outcome);
        Assert.False(result.Settings.Serial.Enabled);
    }

    [Fact]
    public void SecondBackupInTheSameSecond_GetsADistinctName()
    {
        File.WriteAllText(ConfigPath, "not json");
        using (var first = CreateCoordinator())
        {
            first.Load();
        }

        File.WriteAllText(ConfigPath, "still not json");
        using var second = CreateCoordinator();
        var result = second.Load();

        Assert.Equal("config.invalid-20261003-093000-2.json", Path.GetFileName(result.BackupPath));
        Assert.Equal("still not json", File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public void Changes_AreSavedOnce_AfterTheQuietPeriod()
    {
        using var coordinator = CreateCoordinator();
        var holder = new SettingsHolder(coordinator.Load().Settings);
        coordinator.Attach(holder);
        File.Delete(ConfigPath);

        // A slider drag: many publishes, each within the debounce period of the previous one.
        for (var step = 1; step <= 5; step++)
        {
            var opacity = step / 10f;
            Assert.True(holder.TryPublish(holder.Current.Settings with { Overlay = holder.Current.Settings.Overlay with { Opacity = opacity } }, out _));
            _time.Advance(Debounce / 2);
            Assert.False(File.Exists(ConfigPath));
        }

        _time.Advance(Debounce);

        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(0.5f, AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath)).Overlay.Opacity);
    }

    [Fact]
    public void Flush_WritesAPendingChangeImmediately()
    {
        using var coordinator = CreateCoordinator();
        var holder = new SettingsHolder(coordinator.Load().Settings);
        coordinator.Attach(holder);

        Assert.True(holder.TryPublish(holder.Current.Settings with { Capture = new CaptureSettings { MaxFps = 30 } }, out _));
        coordinator.Flush();

        Assert.Equal(30, AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath)).Capture.MaxFps);
    }

    [Fact]
    public void Dispose_WritesAPendingChange()
    {
        var coordinator = CreateCoordinator();
        var holder = new SettingsHolder(coordinator.Load().Settings);
        coordinator.Attach(holder);
        Assert.True(holder.TryPublish(holder.Current.Settings with { Overlay = holder.Current.Settings.Overlay with { Enabled = false } }, out _));

        coordinator.Dispose();

        Assert.False(AppSettingsStore.Deserialize(File.ReadAllText(ConfigPath)).Overlay.Enabled);
    }

    [Fact]
    public void FailedSave_IsReportedWithoutThrowing_AndRetriedLater()
    {
        // The configured folder is a file, so creating the directory fails.
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        var path = Path.Combine(blocker, "config.json");
        using var coordinator = new SettingsCoordinator(path, NullLogger<SettingsCoordinator>.Instance, _time, Debounce);

        var result = coordinator.Load();
        Assert.Equal(SettingsLoadOutcome.CreatedDefaults, result.Outcome);
        Assert.NotNull(coordinator.LastSaveError);

        var holder = new SettingsHolder(result.Settings);
        coordinator.Attach(holder);
        Assert.True(holder.TryPublish(holder.Current.Settings with { Capture = new CaptureSettings { MaxFps = 30 } }, out _));
        _time.Advance(Debounce);
        Assert.NotNull(coordinator.LastSaveError);

        // Once the folder can be created, the retained change is written by the next flush.
        File.Delete(blocker);
        coordinator.Flush();

        Assert.Null(coordinator.LastSaveError);
        Assert.Equal(30, AppSettingsStore.Deserialize(File.ReadAllText(path)).Capture.MaxFps);
    }

    [Fact]
    public void LockedFile_IsNotOverwritten()
    {
        AppSettingsStore.Save(ConfigPath, new AppSettings { Capture = new CaptureSettings { MaxFps = 30 } });
        var original = File.ReadAllText(ConfigPath);
        using var coordinator = CreateCoordinator();

        SettingsLoadResult result;
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = coordinator.Load();
        }

        Assert.Equal(SettingsLoadOutcome.Unreadable, result.Outcome);
        Assert.False(coordinator.AutosaveEnabled);

        var holder = new SettingsHolder(result.Settings);
        coordinator.Attach(holder);
        Assert.True(holder.TryPublish(holder.Current.Settings with { Capture = new CaptureSettings { MaxFps = 45 } }, out _));
        coordinator.Flush();

        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ResetInvalidSections_ResetsOnlyTheSectionsWithErrors()
    {
        var settings = new AppSettings
        {
            Capture = new CaptureSettings { MaxFps = 0 },
            Overlay = new OverlaySettings { Opacity = 0.5f },
            Letterbox = new LetterboxSettings { BlackLevel = 999 },
        };

        var repaired = SettingsCoordinator.ResetInvalidSections(settings, settings.Validate());

        Assert.Equal(new CaptureSettings(), repaired.Capture);
        Assert.Equal(new LetterboxSettings(), repaired.Letterbox);
        Assert.Equal(0.5f, repaired.Overlay.Opacity);
        Assert.DoesNotContain(repaired.Validate(), issue => issue.Severity == SettingsIssueSeverity.Error);
    }

    private SettingsCoordinator CreateCoordinator() =>
        new(ConfigPath, NullLogger<SettingsCoordinator>.Instance, _time, Debounce);
}

public sealed class SettingsFormValuesTests
{
    [Fact]
    public void From_Defaults_ShowsTheBalancedLookInUiUnits_AndTheLedStripOff()
    {
        var values = SettingsFormValues.From(new AppSettings());

        Assert.True(values.OverlayEnabled);
        Assert.Equal(85, values.InnerIntensityPercent);
        Assert.Equal(3, values.InnerGlowPercent);
        Assert.Equal(40, values.WashIntensityPercent);
        Assert.Equal(20, values.SpreadWidthPercent);
        Assert.Equal(25, values.BlurRadiusPercent);
        Assert.Equal(OverlayBlendMode.Screen, values.BlendMode);
        Assert.Equal(100, values.OverlayBrightnessPercent);
        Assert.Equal(85, values.OpacityPercent);
        Assert.Equal(1.35, values.Saturation);
        Assert.Equal(1.15, values.Contrast);
        Assert.Equal(1.1, values.LuminanceGain);
        Assert.True(values.KeepPictureClear);
        Assert.Equal(60, values.TargetFps);
        Assert.False(values.SerialEnabled);
        Assert.Equal(string.Empty, values.PortName);
        Assert.Equal(1_000_000, values.BaudRate);
    }

    [Fact]
    public void ApplyTo_WithoutEdits_ChangesNothing()
    {
        var settings = new AppSettings
        {
            Overlay = new OverlaySettings { Opacity = 0.8543f, SpreadFraction = 0.0437f, Saturation = 1.337f },
            LedLayout = new LedLayoutSettings { TopCount = 40 },
        };

        var roundTripped = SettingsFormValues.From(settings).ApplyTo(settings);

        // Untouched values keep their exact stored value despite the display rounding.
        Assert.Equal(settings, roundTripped);
    }

    [Fact]
    public void ApplyTo_ChangesOnlyTheEditedValues()
    {
        var settings = new AppSettings();
        var values = SettingsFormValues.From(settings) with
        {
            OverlayBrightnessPercent = 50,
            SpreadWidthPercent = 45,
            Saturation = 1.9,
            BlendMode = OverlayBlendMode.Additive,
            KeepPictureClear = false,
            TargetFps = 30,
        };

        var updated = values.ApplyTo(settings);

        Assert.Equal(
            settings.Overlay with { Brightness = 0.5f, SpreadFraction = 0.45f, Saturation = 1.9f, BlendMode = OverlayBlendMode.Additive, KeepPictureClear = false },
            updated.Overlay);
        Assert.Equal(30, updated.Capture.MaxFps);
        Assert.Equal(settings.Processing, updated.Processing);
    }

    [Theory]
    [InlineData(0.1, 0.005f)]
    [InlineData(80, 0.5f)]
    [InlineData(double.NaN, 0.005f)]
    public void ApplyTo_ClampsTheSpreadWidthToTheSliderRange(double percent, float expected)
    {
        var settings = new AppSettings();
        var updated = (SettingsFormValues.From(settings) with { SpreadWidthPercent = percent }).ApplyTo(settings);

        Assert.Equal(expected, updated.Overlay.SpreadFraction);
        Assert.DoesNotContain(updated.Validate(), issue => issue.Severity == SettingsIssueSeverity.Error);
    }

    [Fact]
    public void ApplyTo_ClampsTheColorGrade()
    {
        var settings = new AppSettings();
        var updated = (SettingsFormValues.From(settings) with { Saturation = 3, Contrast = 0.1, LuminanceGain = 9 }).ApplyTo(settings);

        Assert.Equal(2f, updated.Overlay.Saturation);
        Assert.Equal(0.5f, updated.Overlay.Contrast);
        Assert.Equal(2f, updated.Overlay.LuminanceGain);
    }

    [Theory]
    [InlineData(OverlayPreset.Subtle)]
    [InlineData(OverlayPreset.Cinematic)]
    public void Presets_SurviveARoundTripThroughTheForm(OverlayPreset preset)
    {
        var settings = new AppSettings { Overlay = OverlayPresets.Apply(new OverlaySettings(), preset) };

        var roundTripped = SettingsFormValues.From(settings).ApplyTo(settings);

        Assert.Equal(preset, OverlayPresets.Detect(roundTripped.Overlay));
    }

    [Fact]
    public void ApplyTo_TrimsThePortName()
    {
        var settings = new AppSettings();
        var updated = (SettingsFormValues.From(settings) with { SerialEnabled = true, PortName = " COM4 " }).ApplyTo(settings);

        Assert.Equal("COM4", updated.Serial.PortName);
        Assert.True(updated.Serial.Enabled);
    }
}
