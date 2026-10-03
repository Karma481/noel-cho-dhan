using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AmbientLight.Core.Color;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;

namespace AmbientLight.Overlay.Rendering;

/// <summary>
/// Decides whether the glow must be redrawn. A redraw (and the Present that wakes DWM) happens only when
/// the display colors, the zone layout, the overlay settings or the monitor geometry actually changed.
/// While the picture is static the overlay therefore costs no GPU time at all.
/// </summary>
/// <remarks>
/// Allocation-free: colors are compared as raw bytes (vectorized) against a pre-allocated copy, the zone
/// list by the identity of its immutable backing array, the settings by record equality.
/// </remarks>
internal sealed class RedrawTracker
{
    private readonly ColorRgb[] _drawnColors = new ColorRgb[LedLayoutSettings.MaxLedCount];
    private int _drawnCount = -1;
    private GlowLayout _drawnLayout;
    private OverlaySettings? _drawnSettings;
    private ImmutableArray<ZoneConfig> _drawnZones;

    /// <summary>True when the given state differs from what is currently on screen.</summary>
    public bool NeedsRedraw(ReadOnlySpan<ColorRgb> colors, in GlowLayout layout, OverlaySettings settings, ImmutableArray<ZoneConfig> zones)
    {
        if (_drawnCount != colors.Length || _drawnLayout != layout || _drawnSettings != settings || _drawnZones != zones)
        {
            return true;
        }

        var drawn = MemoryMarshal.AsBytes(_drawnColors.AsSpan(0, _drawnCount));
        return !MemoryMarshal.AsBytes(colors).SequenceEqual(drawn);
    }

    /// <summary>Records what was successfully presented.</summary>
    public void MarkDrawn(ReadOnlySpan<ColorRgb> colors, in GlowLayout layout, OverlaySettings settings, ImmutableArray<ZoneConfig> zones)
    {
        colors.CopyTo(_drawnColors);
        _drawnCount = colors.Length;
        _drawnLayout = layout;
        _drawnSettings = settings;
        _drawnZones = zones;
    }

    /// <summary>Forces the next check to report a redraw (device recreated, window shown again).</summary>
    public void Invalidate() => _drawnCount = -1;
}
