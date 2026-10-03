// ZoneReduce.hlsl
//
// Reduces the duplicated desktop image to one linear-light color per LED zone.
//
// Dispatch: one thread group per zone, Dispatch(ZoneCount, 1, 1).
// Each group takes SamplesPerAxis x SamplesPerAxis bilinear samples spread evenly over the zone,
// decodes them to linear light normalized to SDR reference white (1.0 = SDR white), averages them
// with a parallel tree reduction in group-shared memory, then maps the average into the 0..1 LED
// range (gamut clip + highlight roll-off for HDR).
//
// Averaging happens in linear light on purpose: averaging gamma-encoded values darkens mixed
// regions (half black / half white would read as ~22% instead of 50% light).
//
// Bindings must match GpuZoneReducer.cs and ZoneReduceConstants.cs.

#define THREADS_PER_ZONE 64

// Values of ZoneReduceConstants.Encoding (SurfaceEncoding in C#).
#define ENCODING_SRGB_UNORM      0  // 8-bit UNORM holding sRGB-encoded values (SDR desktop)
#define ENCODING_SCRGB_LINEAR    1  // FP16 scRGB: linear, BT.709 primaries, 1.0 = 80 nits (HDR desktop)
#define ENCODING_LINEAR_SDR      2  // *_SRGB view: hardware already decoded to linear 0..1

cbuffer ZoneReduceConstants : register(b0)
{
    uint  ZoneCount;       // Number of valid zones (== dispatched groups).
    uint  SamplesPerAxis;  // N, giving N*N samples per zone.
    uint  Encoding;        // ENCODING_* value describing the desktop surface.
    uint  ToneMapEnabled;  // 0 = hard clip above SDR white, 1 = smooth highlight roll-off.
    float SdrWhiteScale;   // scRGB value of SDR reference white (SDR white nits / 80).
    float PeakWhite;       // Display peak luminance relative to SDR white (>= 1).
    float KneeStart;       // Relative luminance where the roll-off begins (0..1).
    float Reserved;
};

// Mirrors AmbientLight.Core.Zones.ZoneConfig (24 bytes). Region is already transformed from
// visible-desktop space into desktop-texture space, so display rotation is handled on the CPU.
struct Zone
{
    uint   Index;
    uint   Edge;
    float4 Region; // x, y, width, height in normalized texture coordinates
};

StructuredBuffer<Zone>     Zones      : register(t0);
Texture2D<float4>          Desktop    : register(t1);
SamplerState               LinearClamp : register(s0);
RWStructuredBuffer<float4> ZoneColors : register(u0);

groupshared float3 PartialSums[THREADS_PER_ZONE];

static const float3 Rec709Luma = float3(0.2126, 0.7152, 0.0722);

// IEC 61966-2-1 sRGB EOTF, branch-free.
float3 SrgbToLinear(float3 encoded)
{
    float3 c = saturate(encoded);
    float3 low = c / 12.92;
    float3 high = pow((c + 0.055) / 1.055, 2.4);
    return lerp(high, low, step(c, 0.04045));
}

// Converts one texel to linear light where 1.0 = SDR reference white.
// scRGB values are kept unclamped (they may be negative for wide-gamut colors or above 1 for
// highlights) so that averaging stays physically correct; clipping happens once, after averaging.
float3 DecodeTexel(float3 texel)
{
    if (Encoding == ENCODING_SCRGB_LINEAR)
    {
        return texel / SdrWhiteScale;
    }

    if (Encoding == ENCODING_LINEAR_SDR)
    {
        return saturate(texel);
    }

    return SrgbToLinear(texel);
}

// Brings colors outside the BT.709 gamut (negative components in scRGB) back inside by
// desaturating towards their own luminance, which keeps brightness and approximate hue instead
// of the hue shift a per-channel clamp would cause.
float3 GamutClip(float3 color)
{
    float minimum = min(color.r, min(color.g, color.b));
    if (minimum >= 0.0)
    {
        return color;
    }

    float luma = dot(color, Rec709Luma);
    if (luma <= 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    return luma + (color - luma) * (luma / (luma - minimum));
}

// Maps relative luminance [0, PeakWhite] into [0, 1] for the LEDs.
// Identity below KneeStart; above it an extended-Reinhard curve that is C1-continuous at the
// knee (same value and slope) and reaches exactly 1.0 at the display's peak luminance.
// Applied to the maximum channel and scaled uniformly so hue and saturation are preserved.
float3 RollOffHighlights(float3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (ToneMapEnabled == 0 || PeakWhite <= 1.0 || peak <= KneeStart)
    {
        return saturate(color);
    }

    float range = 1.0 - KneeStart;
    float t = (peak - KneeStart) / range;
    float tWhite = (PeakWhite - KneeStart) / range;
    float compressed = t * (1.0 + t / (tWhite * tWhite)) / (1.0 + t);
    float mapped = min(KneeStart + range * compressed, 1.0);
    return saturate(color * (mapped / peak));
}

float3 MapToLedRange(float3 linearColor)
{
    if (Encoding == ENCODING_SCRGB_LINEAR)
    {
        return RollOffHighlights(GamutClip(linearColor));
    }

    return saturate(linearColor);
}

[numthreads(THREADS_PER_ZONE, 1, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint threadIndex : SV_GroupIndex)
{
    uint zoneIndex = groupId.x;
    Zone zone = Zones[zoneIndex];

    uint samplesPerAxis = max(SamplesPerAxis, 1u);
    uint sampleCount = samplesPerAxis * samplesPerAxis;
    float inverseSamples = 1.0 / (float)samplesPerAxis;

    float3 sum = float3(0.0, 0.0, 0.0);
    for (uint sampleIndex = threadIndex; sampleIndex < sampleCount; sampleIndex += THREADS_PER_ZONE)
    {
        uint column = sampleIndex % samplesPerAxis;
        uint row = sampleIndex / samplesPerAxis;
        float2 cell = (float2(column, row) + 0.5) * inverseSamples;
        float2 uv = zone.Region.xy + cell * zone.Region.zw;
        sum += DecodeTexel(Desktop.SampleLevel(LinearClamp, uv, 0.0).rgb);
    }

    PartialSums[threadIndex] = sum;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint stride = THREADS_PER_ZONE / 2; stride > 0; stride >>= 1)
    {
        if (threadIndex < stride)
        {
            PartialSums[threadIndex] += PartialSums[threadIndex + stride];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (threadIndex == 0)
    {
        float3 average = PartialSums[0] / (float)sampleCount;
        ZoneColors[zoneIndex] = float4(MapToLedRange(average), 1.0);
    }
}
