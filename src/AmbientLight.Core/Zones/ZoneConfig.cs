using System.Runtime.InteropServices;

namespace AmbientLight.Core.Zones;

/// <summary>
/// One sampling zone, mapped 1:1 to one physical LED and one segment of the virtual overlay.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Index"/> is the LED's position along the physical strip, so a zone array sorted by
/// <see cref="Index"/> is already in wire order for the serial output.
/// </para>
/// <para>
/// The struct is blittable (24 bytes: int, int, float4) so the whole layout uploads directly into an
/// HLSL <c>StructuredBuffer&lt;Zone&gt;</c> consumed by the GPU reduction shader:
/// <code>struct Zone { uint index; uint edge; float4 region; };</code>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ZoneConfig(int Index, ScreenEdge Edge, NormalizedRect Region)
{
    /// <summary>Size in bytes of one zone in the GPU structured buffer.</summary>
    public const int GpuStride = 24;
}
