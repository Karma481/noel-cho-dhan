using Vortice.DXGI;

namespace AmbientLight.Capture.ColorSpace;

/// <summary>Immutable description of the monitor being captured, refreshed whenever duplication is (re)created.</summary>
/// <param name="DeviceName">GDI device name, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="AdapterName">GPU description, for diagnostics.</param>
/// <param name="DesktopWidth">Visible desktop width in pixels (after rotation).</param>
/// <param name="DesktopHeight">Visible desktop height in pixels (after rotation).</param>
/// <param name="Rotation">Rotation of the visible desktop relative to the duplicated surface.</param>
/// <param name="ColorSpace">Output color space reported by <c>IDXGIOutput6</c>; SDR when unavailable.</param>
/// <param name="MaxLuminanceNits">Panel peak luminance from EDID; 0 when unknown.</param>
/// <param name="SdrWhiteNits">SDR reference white used by DWM in HDR mode ("SDR content brightness").</param>
public sealed record DisplayOutputInfo(
    string DeviceName,
    string AdapterName,
    int DesktopWidth,
    int DesktopHeight,
    ModeRotation Rotation,
    ColorSpaceType ColorSpace,
    float MaxLuminanceNits,
    float SdrWhiteNits)
{
    /// <summary>True when Windows HDR ("Use HDR") is on for this monitor: ST.2084 PQ with BT.2020 primaries.</summary>
    public bool IsHdr => ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
}
