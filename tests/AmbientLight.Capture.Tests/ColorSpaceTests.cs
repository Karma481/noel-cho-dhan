using System.Numerics;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using Vortice.DXGI;

namespace AmbientLight.Capture.Tests;

public sealed class ColorSpaceTests
{
    private static DisplayOutputInfo Output(bool hdr, float sdrWhiteNits = 240f, float peakNits = 1000f) => new(
        @"\\.\DISPLAY1",
        "Test GPU",
        3840,
        2160,
        ModeRotation.Identity,
        hdr ? ColorSpaceType.RgbFullG2084NoneP2020 : ColorSpaceType.RgbFullG22NoneP709,
        peakNits,
        sdrWhiteNits);

    [Fact]
    public void SdrSurface_IsDecodedAsSrgb_WithoutScaling()
    {
        var mapping = ColorSpaceMapping.Resolve(Format.B8G8R8A8_UNorm, Output(hdr: false), new CaptureSettings());

        Assert.Equal(SurfaceEncoding.SrgbUnorm, mapping.Encoding);
        Assert.Equal(1f, mapping.SdrWhiteScale);
        Assert.False(mapping.ToneMapEnabled);
    }

    [Fact]
    public void HdrSurface_NormalizesToSdrWhite_AndRollsOffToThePanelPeak()
    {
        var mapping = ColorSpaceMapping.Resolve(Format.R16G16B16A16_Float, Output(hdr: true), new CaptureSettings());

        Assert.Equal(SurfaceEncoding.ScRgbLinear, mapping.Encoding);
        Assert.Equal(3f, mapping.SdrWhiteScale);                 // 240 nits / 80 nits
        Assert.Equal(1000f / 240f, mapping.PeakWhite, precision: 5);
        Assert.True(mapping.ToneMapEnabled);
        Assert.Equal(ColorSpaceMapping.DefaultKneeStart, mapping.KneeStart);
    }

    [Fact]
    public void HdrSurface_FallsBack_WhenWindowsReportsNoSdrWhiteOrPeak()
    {
        var mapping = ColorSpaceMapping.Resolve(
            Format.R16G16B16A16_Float,
            Output(hdr: true, sdrWhiteNits: 0f, peakNits: 0f),
            new CaptureSettings());

        Assert.Equal(ColorSpaceMapping.FallbackSdrWhiteNits / ColorSpaceMapping.ScRgbReferenceNits, mapping.SdrWhiteScale);
        Assert.Equal(1f, mapping.PeakWhite);
        Assert.False(mapping.ToneMapEnabled);
    }

    [Fact]
    public void HdrToneMapping_CanBeDisabled()
    {
        var mapping = ColorSpaceMapping.Resolve(
            Format.R16G16B16A16_Float,
            Output(hdr: true),
            new CaptureSettings { HdrToneMapping = false });

        Assert.False(mapping.ToneMapEnabled);
        Assert.Equal(3f, mapping.SdrWhiteScale);
    }

    [Fact]
    public void Fp16SurfaceFromSdrDesktop_HasWhiteAtOne()
    {
        var mapping = ColorSpaceMapping.Resolve(Format.R16G16B16A16_Float, Output(hdr: false), new CaptureSettings());

        Assert.Equal(SurfaceEncoding.ScRgbLinear, mapping.Encoding);
        Assert.Equal(1f, mapping.SdrWhiteScale);
        Assert.False(mapping.ToneMapEnabled);
    }

    [Theory]
    [InlineData(Format.B8G8R8A8_UNorm, SurfaceEncoding.SrgbUnorm)]
    [InlineData(Format.R8G8B8A8_UNorm, SurfaceEncoding.SrgbUnorm)]
    [InlineData(Format.B8G8R8A8_UNorm_SRgb, SurfaceEncoding.LinearSdr)]
    [InlineData(Format.R16G16B16A16_Float, SurfaceEncoding.ScRgbLinear)]
    public void EncodingOf_KnownFormats(Format format, SurfaceEncoding expected)
    {
        Assert.Equal(expected, ColorSpaceMapping.EncodingOf(format));
    }

    [Fact]
    public void EncodingOf_RejectsUnexpectedFormats()
    {
        Assert.Throws<NotSupportedException>(() => ColorSpaceMapping.EncodingOf(Format.R10G10B10A2_UNorm));
    }

    [Fact]
    public void PreferredFormats_OfferFp16OnlyForHdr()
    {
        Assert.Equal([Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm], ColorSpaceMapping.PreferredDuplicationFormats(isHdr: true));
        Assert.Equal([Format.B8G8R8A8_UNorm], ColorSpaceMapping.PreferredDuplicationFormats(isHdr: false));
    }

    [Fact]
    public void Rotation_Identity_LeavesRectUnchanged()
    {
        var rect = new NormalizedRect(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.Equal(rect, SurfaceOrientation.ToTextureSpace(rect, ModeRotation.Identity));
        Assert.Equal(rect, SurfaceOrientation.ToTextureSpace(rect, ModeRotation.Unspecified));
    }

    [Fact]
    public void Rotation_MapsTheVisibleTopEdge_ToTheExpectedTextureEdge()
    {
        // A strip along the top of the visible desktop.
        var top = new NormalizedRect(0f, 0f, 1f, 0.1f);

        // 90°: visible top (v = 0) -> texture u = 0, i.e. the left column of the texture.
        Assert.Equal(new NormalizedRect(0f, 0f, 0.1f, 1f), SurfaceOrientation.ToTextureSpace(top, ModeRotation.Rotate90));

        // 180°: visible top -> texture bottom.
        AssertClose(new NormalizedRect(0f, 0.9f, 1f, 0.1f), SurfaceOrientation.ToTextureSpace(top, ModeRotation.Rotate180));

        // 270°: visible top (v = 0) -> texture u = 1, i.e. the right column.
        AssertClose(new NormalizedRect(0.9f, 0f, 0.1f, 1f), SurfaceOrientation.ToTextureSpace(top, ModeRotation.Rotate270));
    }

    [Theory]
    [InlineData(ModeRotation.Rotate90, ModeRotation.Rotate270)]
    [InlineData(ModeRotation.Rotate270, ModeRotation.Rotate90)]
    [InlineData(ModeRotation.Rotate180, ModeRotation.Rotate180)]
    public void Rotation_ComposedWithItsInverse_IsIdentity(ModeRotation rotation, ModeRotation inverse)
    {
        var rect = new NormalizedRect(0.15f, 0.25f, 0.2f, 0.05f);

        AssertClose(rect, SurfaceOrientation.ToTextureSpace(SurfaceOrientation.ToTextureSpace(rect, rotation), inverse));
    }

    [Theory]
    [InlineData(ModeRotation.Identity)]
    [InlineData(ModeRotation.Rotate90)]
    [InlineData(ModeRotation.Rotate180)]
    [InlineData(ModeRotation.Rotate270)]
    public void Rotation_KeepsEveryZoneInsideTheTexture(ModeRotation rotation)
    {
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings());
        var transformed = new ZoneConfig[zones.Length];

        SurfaceOrientation.TransformZones(zones.AsSpan(), NormalizedRect.Full, rotation, transformed);

        for (var i = 0; i < zones.Length; i++)
        {
            Assert.Equal(zones[i].Index, transformed[i].Index);
            Assert.Equal(zones[i].Edge, transformed[i].Edge);
            Assert.True(transformed[i].Region.IsValid, $"Zone {i} at {rotation}: {transformed[i].Region}");
        }
    }

    [Theory]
    [InlineData(ModeRotation.Identity)]
    [InlineData(ModeRotation.Rotate90)]
    [InlineData(ModeRotation.Rotate180)]
    [InlineData(ModeRotation.Rotate270)]
    public void Affine_AgreesWithRectTransform(ModeRotation rotation)
    {
        var (origin, axisU, axisV) = SurfaceOrientation.VisibleToTexture(rotation);
        var rect = new NormalizedRect(0.2f, 0.3f, 0.1f, 0.05f);
        var expected = SurfaceOrientation.ToTextureSpace(rect, rotation);

        // Map both opposite corners of the visible rect; their bounding box must equal the rect transform.
        var a = origin + (rect.X * axisU) + (rect.Y * axisV);
        var b = origin + (rect.Right * axisU) + (rect.Bottom * axisV);
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);

        AssertClose(expected, new NormalizedRect(min.X, min.Y, max.X - min.X, max.Y - min.Y));
    }

    [Fact]
    public void TransformZones_PlacesZonesInsideTheLetterboxedContent()
    {
        // 2.39:1 film on a 16:9 screen: bars of 12.8% top and bottom.
        var content = new NormalizedRect(0f, 0.128f, 1f, 0.744f);
        var zones = ZoneLayoutBuilder.Build(new LedLayoutSettings { SampleDepth = 0.1f });
        var transformed = new ZoneConfig[zones.Length];

        SurfaceOrientation.TransformZones(zones.AsSpan(), content, ModeRotation.Identity, transformed);

        Assert.All(transformed, zone =>
        {
            Assert.True(zone.Region.Y >= content.Y - 1e-6f, $"{zone.Region} starts in the top bar");
            Assert.True(zone.Region.Bottom <= content.Bottom + 1e-6f, $"{zone.Region} reaches into the bottom bar");
        });
        var top = transformed.First(zone => zone.Edge == ScreenEdge.Top);
        Assert.Equal(0.128f, top.Region.Y, precision: 5);
        Assert.Equal(0.0744f, top.Region.Height, precision: 5);
    }

    [Fact]
    public void VisibleSize_SwapsDimensions_ForPortraitRotations()
    {
        Assert.Equal((2160, 3840), SurfaceOrientation.VisibleSize(3840, 2160, ModeRotation.Rotate90));
        Assert.Equal((2160, 3840), SurfaceOrientation.VisibleSize(3840, 2160, ModeRotation.Rotate270));
        Assert.Equal((3840, 2160), SurfaceOrientation.VisibleSize(3840, 2160, ModeRotation.Rotate180));
    }

    private static void AssertClose(NormalizedRect expected, NormalizedRect actual)
    {
        const int Precision = 5;
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
