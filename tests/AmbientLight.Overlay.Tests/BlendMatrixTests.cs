using AmbientLight.Core.Settings;
using AmbientLight.Overlay.Rendering;

namespace AmbientLight.Overlay.Tests;

/// <summary>
/// The color matrix applied to the premultiplied glow before DWM composes it as <c>glow + screen × (1 − alpha)</c>.
/// Evaluated here on the CPU exactly as Direct2D does in straight-alpha mode: <c>out = [r g b a 1] × M</c>.
/// </summary>
public sealed class BlendMatrixTests
{
    [Theory]
    [InlineData(OverlayBlendMode.Normal)]
    [InlineData(OverlayBlendMode.Screen)]
    [InlineData(OverlayBlendMode.Additive)]
    public void ColorPassesThroughUnchanged(OverlayBlendMode mode)
    {
        var (r, g, b, _) = Apply(mode, 0.8f, 0.2f, 0.6f, 0.9f);

        Assert.Equal((0.8f, 0.2f, 0.6f), (r, g, b));
    }

    [Fact]
    public void Normal_KeepsAlpha()
    {
        Assert.Equal(0.9f, Apply(OverlayBlendMode.Normal, 0.8f, 0.2f, 0.6f, 0.9f).A, precision: 6);
    }

    [Fact]
    public void Additive_DropsAlpha_SoDwmAddsTheGlow()
    {
        Assert.Equal(0f, Apply(OverlayBlendMode.Additive, 0.8f, 0.2f, 0.6f, 0.9f).A);
    }

    [Fact]
    public void Screen_UsesTheGlowLuminanceAsAlpha()
    {
        // Black glow is fully transparent; white glow fully opaque; colors in between by their luminance.
        Assert.Equal(0f, Apply(OverlayBlendMode.Screen, 0f, 0f, 0f, 0.9f).A);
        Assert.Equal(1f, Apply(OverlayBlendMode.Screen, 1f, 1f, 1f, 1f).A, precision: 5);
        Assert.Equal(0.0722f, Apply(OverlayBlendMode.Screen, 0f, 0f, 1f, 1f).A, precision: 5);
    }

    [Fact]
    public void Screen_ApproximatesAPerChannelScreenBlend_BetterThanOpaquePainting()
    {
        // Pink-purple glow over mid grey: what DWM produces versus the ideal screen blend 1 - (1 - glow)(1 - screen).
        (float R, float G, float B) glow = (0.8f, 0.2f, 0.6f);
        const float Under = 0.5f;
        var alpha = Apply(OverlayBlendMode.Screen, glow.R, glow.G, glow.B, 1f).A;

        float Composed(float channel) => MathF.Min(1f, channel + (Under * (1f - alpha)));
        static float Ideal(float channel) => 1f - ((1f - channel) * (1f - Under));
        float Painted(float channel) => channel; // Normal mode with an opaque glow hides what is under it.

        var screenError = MathF.Abs(Composed(glow.R) - Ideal(glow.R)) + MathF.Abs(Composed(glow.G) - Ideal(glow.G)) + MathF.Abs(Composed(glow.B) - Ideal(glow.B));
        var paintedError = MathF.Abs(Painted(glow.R) - Ideal(glow.R)) + MathF.Abs(Painted(glow.G) - Ideal(glow.G)) + MathF.Abs(Painted(glow.B) - Ideal(glow.B));
        Assert.True(screenError < paintedError / 2f, $"Screen error {screenError:F3}, painted error {paintedError:F3}.");
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GlowRenderer.BlendMatrix((OverlayBlendMode)42));
    }

    private static (float R, float G, float B, float A) Apply(OverlayBlendMode mode, float r, float g, float b, float a)
    {
        var m = GlowRenderer.BlendMatrix(mode);
        return (
            (r * m.M11) + (g * m.M21) + (b * m.M31) + (a * m.M41) + m.M51,
            (r * m.M12) + (g * m.M22) + (b * m.M32) + (a * m.M42) + m.M52,
            (r * m.M13) + (g * m.M23) + (b * m.M33) + (a * m.M43) + m.M53,
            (r * m.M14) + (g * m.M24) + (b * m.M34) + (a * m.M44) + m.M54);
    }
}
