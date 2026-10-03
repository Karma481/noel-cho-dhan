namespace AmbientLight.Core.Zones;

/// <summary>The screen edge a zone belongs to. Values are fixed because they are uploaded to HLSL.</summary>
public enum ScreenEdge
{
    /// <summary>Top edge, sampled downward from y = 0.</summary>
    Top = 0,

    /// <summary>Right edge, sampled leftward from x = 1.</summary>
    Right = 1,

    /// <summary>Bottom edge, sampled upward from y = 1.</summary>
    Bottom = 2,

    /// <summary>Left edge, sampled rightward from x = 0.</summary>
    Left = 3,
}

/// <summary>The screen corner (seen from the front) where the first LED of the strip sits.</summary>
public enum StripStartCorner
{
    /// <summary>First LED at the top-left corner.</summary>
    TopLeft = 0,

    /// <summary>First LED at the top-right corner.</summary>
    TopRight = 1,

    /// <summary>First LED at the bottom-right corner.</summary>
    BottomRight = 2,

    /// <summary>First LED at the bottom-left corner.</summary>
    BottomLeft = 3,
}

/// <summary>The direction the strip runs around the screen, seen from the front.</summary>
public enum StripDirection
{
    /// <summary>Top edge left-to-right, then right edge downward, and so on.</summary>
    Clockwise = 0,

    /// <summary>Left edge downward from the top-left, then bottom edge left-to-right, and so on.</summary>
    CounterClockwise = 1,
}
