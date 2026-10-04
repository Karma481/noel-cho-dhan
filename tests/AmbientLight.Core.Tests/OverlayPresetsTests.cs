using AmbientLight.Core.Settings;

namespace AmbientLight.Core.Tests;

public sealed class OverlayPresetsTests
{
    [Fact]
    public void Defaults_AreTheBalancedPreset()
    {
        Assert.Equal(OverlayPreset.Balanced, OverlayPresets.Detect(new OverlaySettings()));
    }

    [Theory]
    [InlineData(OverlayPreset.Subtle)]
    [InlineData(OverlayPreset.Balanced)]
    [InlineData(OverlayPreset.Cinematic)]
    public void EveryPreset_IsValid_AndRecognizedAfterApplying(OverlayPreset preset)
    {
        var applied = OverlayPresets.Apply(new OverlaySettings(), preset);

        Assert.DoesNotContain(new AppSettings { Overlay = applied }.Validate(), issue => issue.Severity == SettingsIssueSeverity.Error);
        Assert.Equal(preset, OverlayPresets.Detect(applied));
    }

    [Fact]
    public void Apply_KeepsTheOnOffState_PictureProtection_AndRenderResolution()
    {
        var current = new OverlaySettings { Enabled = false, KeepPictureClear = false, ResolutionDivisor = 4 };

        var applied = OverlayPresets.Apply(current, OverlayPreset.Cinematic);

        Assert.False(applied.Enabled);
        Assert.False(applied.KeepPictureClear);
        Assert.Equal(4, applied.ResolutionDivisor);
        Assert.Equal(OverlayPreset.Cinematic, OverlayPresets.Detect(applied));
    }

    [Fact]
    public void Presets_GrowInIntensity()
    {
        var subtle = OverlayPresets.Apply(new OverlaySettings(), OverlayPreset.Subtle);
        var balanced = OverlayPresets.Apply(new OverlaySettings(), OverlayPreset.Balanced);
        var cinematic = OverlayPresets.Apply(new OverlaySettings(), OverlayPreset.Cinematic);

        Assert.True(subtle.SpreadFraction < balanced.SpreadFraction && balanced.SpreadFraction < cinematic.SpreadFraction);
        Assert.True(subtle.WashIntensity < balanced.WashIntensity && balanced.WashIntensity < cinematic.WashIntensity);
        Assert.True(subtle.Saturation < balanced.Saturation && balanced.Saturation < cinematic.Saturation);
        Assert.Equal(OverlayBlendMode.Additive, cinematic.BlendMode);
        Assert.InRange(cinematic.SpreadFraction, 0.2f, 0.5f);
        Assert.InRange(cinematic.BlurRadiusFraction, 0.2f, 0.5f);
    }

    [Fact]
    public void AnyEdit_MakesTheLookCustom()
    {
        var edited = OverlayPresets.Apply(new OverlaySettings(), OverlayPreset.Balanced) with { Saturation = 1.36f };

        Assert.Equal(OverlayPreset.Custom, OverlayPresets.Detect(edited));
    }

    [Fact]
    public void Custom_CannotBeApplied()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayPresets.Apply(new OverlaySettings(), OverlayPreset.Custom));
    }

    [Fact]
    public void BlendMode_IsStoredAsText_AndOldFilesGetTheNewDefaults()
    {
        var json = AppSettingsStore.Serialize(new AppSettings { Overlay = new OverlaySettings { BlendMode = OverlayBlendMode.Additive } });
        Assert.Contains("\"blendMode\": \"Additive\"", json, StringComparison.Ordinal);

        // A Phase 3 file: only the single-layer properties exist.
        var old = AppSettingsStore.Deserialize("""{ "overlay": { "spreadFraction": 0.04, "blurRadiusFraction": 0.08, "opacity": 0.85 } }""");
        Assert.Equal(0.04f, old.Overlay.SpreadFraction);
        Assert.Equal(OverlayBlendMode.Screen, old.Overlay.BlendMode);
        Assert.Equal(new OverlaySettings().InnerGlowFraction, old.Overlay.InnerGlowFraction);
        Assert.DoesNotContain(old.Validate(), issue => issue.Severity == SettingsIssueSeverity.Error);
    }

    [Theory]
    [InlineData("overlay.spreadFraction")]
    [InlineData("overlay.saturation")]
    [InlineData("overlay.contrast")]
    [InlineData("overlay.innerGlowFraction")]
    public void OutOfRangeLookValues_AreRejected(string path)
    {
        var overlay = path switch
        {
            "overlay.spreadFraction" => new OverlaySettings { SpreadFraction = 0.6f },
            "overlay.saturation" => new OverlaySettings { Saturation = 2.5f },
            "overlay.contrast" => new OverlaySettings { Contrast = 0.2f },
            _ => new OverlaySettings { InnerGlowFraction = 0.5f },
        };

        Assert.Contains(new AppSettings { Overlay = overlay }.Validate(), issue => issue.Path == path && issue.Severity == SettingsIssueSeverity.Error);
    }
}
