using System.Runtime.InteropServices;
using AmbientLight.Capture.ColorSpace;

namespace AmbientLight.Capture.Gpu;

/// <summary>
/// CPU mirror of <c>cbuffer ZoneReduceConstants</c> in ZoneReduce.hlsl. Field order, types and the
/// 16-byte register packing must match exactly; <see cref="SizeInBytes"/> is asserted by a unit test.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ZoneReduceConstants(
    uint ZoneCount,
    uint SamplesPerAxis,
    uint Encoding,
    uint ToneMapEnabled,
    float SdrWhiteScale,
    float PeakWhite,
    float KneeStart,
    float Reserved)
{
    /// <summary>Constant buffers must be a multiple of 16 bytes.</summary>
    public const int SizeInBytes = 32;

    public static ZoneReduceConstants Create(int zoneCount, int samplesPerAxis, ColorMappingParameters mapping) => new(
        (uint)zoneCount,
        (uint)samplesPerAxis,
        (uint)mapping.Encoding,
        mapping.ToneMapEnabled ? 1u : 0u,
        mapping.SdrWhiteScale,
        mapping.PeakWhite,
        mapping.KneeStart,
        Reserved: 0f);
}
