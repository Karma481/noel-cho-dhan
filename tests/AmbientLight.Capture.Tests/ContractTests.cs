using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Capture.Gpu;
using AmbientLight.Capture.Interop;
using AmbientLight.Core.Zones;

namespace AmbientLight.Capture.Tests;

/// <summary>
/// The shader and the C# side share struct layouts and magic numbers that no compiler checks across the
/// language boundary. These tests pin them so a change on one side cannot silently break the other.
/// </summary>
public sealed partial class ContractTests
{
    [Fact]
    public void ShaderSource_IsEmbedded()
    {
        var source = ZoneReduceShader.LoadSource();

        Assert.Contains($"void {ZoneReduceShader.EntryPoint}(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadGroupWidth_MatchesShader()
    {
        var source = ZoneReduceShader.LoadSource();

        Assert.Equal(ZoneReduceShader.ThreadsPerZone, ReadDefine(source, "THREADS_PER_ZONE"));
    }

    [Theory]
    [InlineData("ENCODING_SRGB_UNORM", SurfaceEncoding.SrgbUnorm)]
    [InlineData("ENCODING_SCRGB_LINEAR", SurfaceEncoding.ScRgbLinear)]
    [InlineData("ENCODING_LINEAR_SDR", SurfaceEncoding.LinearSdr)]
    public void EncodingValues_MatchShader(string define, SurfaceEncoding encoding)
    {
        Assert.Equal((int)encoding, ReadDefine(ZoneReduceShader.LoadSource(), define));
    }

    [Fact]
    public void ConstantBuffer_FieldOrderAndSize_MatchShader()
    {
        var source = ZoneReduceShader.LoadSource();
        var body = ConstantBufferPattern().Match(source);
        Assert.True(body.Success, "cbuffer ZoneReduceConstants not found.");

        var shaderFields = FieldPattern().Matches(body.Groups["body"].Value).Select(match => match.Groups["name"].Value).ToArray();
        var csharpFields = typeof(ZoneReduceConstants).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name).ToArray();

        Assert.Equal(csharpFields, shaderFields);
        Assert.Equal(ZoneReduceConstants.SizeInBytes, Unsafe.SizeOf<ZoneReduceConstants>());
        Assert.Equal(0, ZoneReduceConstants.SizeInBytes % 16);
    }

    [Fact]
    public void ZoneStruct_MatchesShaderStride()
    {
        Assert.Equal(ZoneConfig.GpuStride, Unsafe.SizeOf<ZoneConfig>());
        Assert.Contains("float4 Region;", ZoneReduceShader.LoadSource(), StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void DisplayConfigStructs_MatchWin32Sizes()
    {
        Assert.Equal(20, sizeof(DisplayConfigInterop.PathSourceInfo));
        Assert.Equal(48, sizeof(DisplayConfigInterop.PathTargetInfo));
        Assert.Equal(72, sizeof(DisplayConfigInterop.PathInfo));
        Assert.Equal(64, sizeof(DisplayConfigInterop.ModeInfo));
        Assert.Equal(20, sizeof(DisplayConfigInterop.DeviceInfoHeader));
        Assert.Equal(84, sizeof(DisplayConfigInterop.SourceDeviceName));
        Assert.Equal(24, sizeof(DisplayConfigInterop.SdrWhiteLevel));
    }

    private static int ReadDefine(string source, string name)
    {
        var match = Regex.Match(source, $@"#define\s+{name}\s+(\d+)");
        Assert.True(match.Success, $"#define {name} not found.");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"cbuffer\s+ZoneReduceConstants[^{]*\{(?<body>[^}]*)\}")]
    private static partial Regex ConstantBufferPattern();

    [GeneratedRegex(@"^\s*(?:uint|float)\s+(?<name>\w+)\s*;", RegexOptions.Multiline)]
    private static partial Regex FieldPattern();
}
