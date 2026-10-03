using AmbientLight.Core.Zones;
using Vortice.DXGI;

namespace AmbientLight.Capture.ColorSpace;

/// <summary>
/// Converts zone rectangles from visible-desktop space (what the user sees and what
/// <see cref="ZoneConfig.Region"/> describes) to duplicated-surface texture space.
/// </summary>
/// <remarks>
/// Desktop Duplication always hands out the surface in the panel's native (unrotated) orientation and
/// reports the display rotation in <c>DXGI_OUTDUPL_DESC.Rotation</c>. Rotating the handful of zone
/// rectangles once on the CPU keeps the shader free of rotation logic.
/// The mappings follow the dirty-rectangle transforms of Microsoft's DesktopDuplication sample:
/// for a visible point (u, v) the texture point is
/// 90° → (v, 1 − u), 180° → (1 − u, 1 − v), 270° → (1 − v, u).
/// </remarks>
public static class SurfaceOrientation
{
    /// <summary>Transforms a visible-space rectangle into texture space for the given rotation.</summary>
    public static NormalizedRect ToTextureSpace(NormalizedRect visible, ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => new NormalizedRect(
            visible.Y,
            1f - visible.Right,
            visible.Height,
            visible.Width),
        ModeRotation.Rotate180 => new NormalizedRect(
            1f - visible.Right,
            1f - visible.Bottom,
            visible.Width,
            visible.Height),
        ModeRotation.Rotate270 => new NormalizedRect(
            1f - visible.Bottom,
            visible.X,
            visible.Height,
            visible.Width),
        _ => visible,
    };

    /// <summary>Visible desktop size for a surface of the given texture size and rotation.</summary>
    public static (int Width, int Height) VisibleSize(int textureWidth, int textureHeight, ModeRotation rotation) =>
        rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270
            ? (textureHeight, textureWidth)
            : (textureWidth, textureHeight);

    /// <summary>Writes texture-space copies of <paramref name="zones"/> into <paramref name="destination"/>.</summary>
    public static void TransformZones(ReadOnlySpan<ZoneConfig> zones, ModeRotation rotation, Span<ZoneConfig> destination)
    {
        if (destination.Length < zones.Length)
        {
            throw new ArgumentException("Destination is smaller than the zone list.", nameof(destination));
        }

        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            destination[i] = zone with { Region = ToTextureSpace(zone.Region, rotation) };
        }
    }
}
