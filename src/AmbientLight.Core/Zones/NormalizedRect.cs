using System.Runtime.InteropServices;

namespace AmbientLight.Core.Zones;

/// <summary>
/// A rectangle in normalized capture-surface coordinates: (0,0) is the top-left corner of the
/// captured output and (1,1) the bottom-right, independent of resolution and DPI.
/// </summary>
/// <remarks>Blittable (4 x float) so it maps 1:1 onto an HLSL <c>float4</c>.</remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NormalizedRect(float X, float Y, float Width, float Height)
{
    private const float Tolerance = 1e-5f;

    /// <summary>The whole surface.</summary>
    public static NormalizedRect Full => new(0f, 0f, 1f, 1f);

    /// <summary>Right edge (X + Width).</summary>
    public float Right => X + Width;

    /// <summary>Bottom edge (Y + Height).</summary>
    public float Bottom => Y + Height;

    /// <summary>True when the rectangle has positive area and lies within the unit square.</summary>
    public bool IsValid =>
        Width > 0f && Height > 0f &&
        X >= 0f && Y >= 0f &&
        Right <= 1f + Tolerance && Bottom <= 1f + Tolerance;

    /// <summary>
    /// Re-expresses this rectangle, given relative to <paramref name="container"/>, in the coordinates
    /// <paramref name="container"/> itself uses. For example a zone covering the top 10% of the picture,
    /// placed within the content area of a letterboxed movie, ends up just below the top black bar.
    /// </summary>
    public NormalizedRect Within(NormalizedRect container) => new(
        container.X + (X * container.Width),
        container.Y + (Y * container.Height),
        Width * container.Width,
        Height * container.Height);

    /// <summary>Converts to integer pixel bounds on a surface of the given size (left, top, right, bottom).</summary>
    public (int Left, int Top, int Right, int Bottom) ToPixels(int surfaceWidth, int surfaceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceHeight);

        var left = (int)MathF.Floor(X * surfaceWidth);
        var top = (int)MathF.Floor(Y * surfaceHeight);
        var right = Math.Max(left + 1, (int)MathF.Ceiling(Right * surfaceWidth));
        var bottom = Math.Max(top + 1, (int)MathF.Ceiling(Bottom * surfaceHeight));
        return (
            Math.Clamp(left, 0, surfaceWidth - 1),
            Math.Clamp(top, 0, surfaceHeight - 1),
            Math.Clamp(right, 1, surfaceWidth),
            Math.Clamp(bottom, 1, surfaceHeight));
    }
}
