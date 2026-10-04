namespace AmbientLight.Core.Settings;

/// <summary>Named looks for the glow overlay.</summary>
public enum OverlayPreset
{
    /// <summary>The settings match no preset (edited by hand).</summary>
    Custom = 0,

    /// <summary>A calm halo close to the bezel: little spill into the picture, gentle colors.</summary>
    Subtle = 1,

    /// <summary>A bright edge glow with a moderate ambient wash and a moderate color boost. The default.</summary>
    Balanced = 2,

    /// <summary>
    /// Maximum impact: additive light, an ambient wash reaching deep into the screen, strongly boosted
    /// saturation and contrast, like the ambient mode of video players on a dark page.
    /// </summary>
    Cinematic = 3,
}

/// <summary>The glow parameters of each <see cref="OverlayPreset"/>, and detection of the preset in effect.</summary>
/// <remarks>
/// A preset sets only the look (layers, blend mode, color grade). Whether the overlay is enabled, whether it keeps
/// the picture clear and its render resolution are left as they are. The preset is not stored: it is recognized from the values, so editing any
/// slider afterwards simply turns the look into <see cref="OverlayPreset.Custom"/>.
/// </remarks>
public static class OverlayPresets
{
    /// <summary>Largest difference between two values still considered equal by <see cref="Detect"/>.</summary>
    private const float Tolerance = 1e-4f;

    private static readonly OverlaySettings SubtleLook = new()
    {
        BlendMode = OverlayBlendMode.Screen,
        Brightness = 0.80f,
        Opacity = 0.70f,
        InnerGlowFraction = 0.02f,
        InnerIntensity = 0.60f,
        SpreadFraction = 0.08f,
        BlurRadiusFraction = 0.12f,
        WashIntensity = 0.20f,
        Saturation = 1.10f,
        Contrast = 1.00f,
        LuminanceGain = 1.00f,
    };

    // Balanced is the OverlaySettings defaults; spelled out so the presets read side by side.
    private static readonly OverlaySettings BalancedLook = new()
    {
        BlendMode = OverlayBlendMode.Screen,
        Brightness = 1.00f,
        Opacity = 0.85f,
        InnerGlowFraction = 0.03f,
        InnerIntensity = 0.85f,
        SpreadFraction = 0.20f,
        BlurRadiusFraction = 0.25f,
        WashIntensity = 0.40f,
        Saturation = 1.35f,
        Contrast = 1.15f,
        LuminanceGain = 1.10f,
    };

    private static readonly OverlaySettings CinematicLook = new()
    {
        BlendMode = OverlayBlendMode.Additive,
        Brightness = 1.00f,
        Opacity = 1.00f,
        InnerGlowFraction = 0.04f,
        InnerIntensity = 1.00f,
        SpreadFraction = 0.35f,
        BlurRadiusFraction = 0.45f,
        WashIntensity = 0.65f,
        Saturation = 1.80f,
        Contrast = 1.35f,
        LuminanceGain = 1.25f,
    };

    /// <summary>The selectable presets, from the calmest to the most intense.</summary>
    public static IReadOnlyList<OverlayPreset> All { get; } = [OverlayPreset.Subtle, OverlayPreset.Balanced, OverlayPreset.Cinematic];

    /// <summary>Returns <paramref name="current"/> with the look of <paramref name="preset"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is <see cref="OverlayPreset.Custom"/> or unknown.</exception>
    public static OverlaySettings Apply(OverlaySettings current, OverlayPreset preset)
    {
        ArgumentNullException.ThrowIfNull(current);
        var look = LookOf(preset);
        return look with
        {
            Enabled = current.Enabled,
            KeepPictureClear = current.KeepPictureClear,
            ResolutionDivisor = current.ResolutionDivisor,
        };
    }

    /// <summary>The preset whose look <paramref name="settings"/> has, or <see cref="OverlayPreset.Custom"/>.</summary>
    public static OverlayPreset Detect(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var preset in All)
        {
            if (SameLook(settings, LookOf(preset)))
            {
                return preset;
            }
        }

        return OverlayPreset.Custom;
    }

    private static OverlaySettings LookOf(OverlayPreset preset) => preset switch
    {
        OverlayPreset.Subtle => SubtleLook,
        OverlayPreset.Balanced => BalancedLook,
        OverlayPreset.Cinematic => CinematicLook,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Only Subtle, Balanced and Cinematic can be applied."),
    };

    private static bool SameLook(OverlaySettings a, OverlaySettings b) =>
        a.BlendMode == b.BlendMode &&
        Close(a.Brightness, b.Brightness) &&
        Close(a.Opacity, b.Opacity) &&
        Close(a.InnerGlowFraction, b.InnerGlowFraction) &&
        Close(a.InnerIntensity, b.InnerIntensity) &&
        Close(a.SpreadFraction, b.SpreadFraction) &&
        Close(a.BlurRadiusFraction, b.BlurRadiusFraction) &&
        Close(a.WashIntensity, b.WashIntensity) &&
        Close(a.Saturation, b.Saturation) &&
        Close(a.Contrast, b.Contrast) &&
        Close(a.LuminanceGain, b.LuminanceGain);

    private static bool Close(float a, float b) => MathF.Abs(a - b) <= Tolerance;
}
