using System.Numerics;
using System.Runtime.InteropServices;
using AmbientLight.Capture.ColorSpace;
using Vortice.DXGI;

namespace AmbientLight.Capture.Gpu;

/// <summary>
/// CPU mirror of <c>cbuffer EdgeProfileConstants</c> (register b1) in ZoneReduce.hlsl: the affine map from
/// visible-desktop coordinates to texture coordinates, so the profile kernel scans what the user sees as
/// rows and columns whatever the display rotation. Layout is pinned by a contract test.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct EdgeProfileConstants(
    Vector2 TextureOrigin,
    Vector2 TextureAxisU,
    Vector2 TextureAxisV,
    Vector2 ProfileReserved)
{
    /// <summary>Two 16-byte constant registers.</summary>
    public const int SizeInBytes = 32;

    public static EdgeProfileConstants For(ModeRotation rotation)
    {
        var (origin, axisU, axisV) = SurfaceOrientation.VisibleToTexture(rotation);
        return new EdgeProfileConstants(origin, axisU, axisV, Vector2.Zero);
    }
}
