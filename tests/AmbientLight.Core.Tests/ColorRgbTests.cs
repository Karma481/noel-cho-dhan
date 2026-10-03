using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AmbientLight.Core.Color;

namespace AmbientLight.Core.Tests;

public sealed class ColorRgbTests
{
    [Fact]
    public void Layout_IsPackedToThreeBytes_InRgbOrder()
    {
        Assert.Equal(ColorRgb.SizeInBytes, Unsafe.SizeOf<ColorRgb>());

        ColorRgb[] colors = [new(1, 2, 3), new(4, 5, 6)];
        var bytes = MemoryMarshal.AsBytes(colors.AsSpan()).ToArray();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, bytes);
    }

    [Fact]
    public void FromNormalized_ClampsAndRounds()
    {
        Assert.Equal(new ColorRgb(255, 0, 128), ColorRgb.FromNormalized(new Vector3(4f, -1f, 0.5f)));
    }

    [Fact]
    public void Normalized_RoundTripsEveryByteValue()
    {
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            var color = new ColorRgb((byte)value, (byte)value, (byte)value);
            Assert.Equal(color, ColorRgb.FromNormalized(color.ToNormalized()));
        }
    }

    [Fact]
    public void Lerp_InterpolatesAndClampsAmount()
    {
        var black = ColorRgb.Black;
        var white = ColorRgb.White;

        Assert.Equal(new ColorRgb(128, 128, 128), ColorRgb.Lerp(black, white, 0.5f));
        Assert.Equal(white, ColorRgb.Lerp(black, white, 2f));
        Assert.Equal(black, ColorRgb.Lerp(black, white, -1f));
    }

    [Fact]
    public void Luma_UsesRec709Weights()
    {
        Assert.Equal(0, ColorRgb.Black.Luma);
        Assert.Equal(255, ColorRgb.White.Luma);
        Assert.True(new ColorRgb(0, 255, 0).Luma > new ColorRgb(255, 0, 0).Luma);
        Assert.True(new ColorRgb(255, 0, 0).Luma > new ColorRgb(0, 0, 255).Luma);
    }

    [Fact]
    public void ToString_FormatsAsHex()
    {
        Assert.Equal("#0AFF80", new ColorRgb(10, 255, 128).ToString());
    }
}
