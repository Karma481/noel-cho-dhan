using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Capture.Gpu;
using AmbientLight.Capture.Interop;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Zones;

namespace AmbientLight.Capture.Tests;

/// <summary>
/// The shader and the C# side share struct layouts and magic numbers that no compiler checks across the
/// language boundary. These tests pin them so a change on one side cannot silently break the other.
/// </summary>
public sealed partial class ContractTests
{
    [Theory]
    [InlineData(ZoneReduceShader.ZoneReduceEntryPoint)]
    [InlineData(ZoneReduceShader.EdgeProfileEntryPoint)]
    public void ShaderSource_IsEmbedded_WithBothKernels(string entryPoint)
    {
        Assert.Contains($"void {entryPoint}(", ZoneReduceShader.LoadSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadGroupWidthsAndProfileResolution_MatchShader()
    {
        var source = ZoneReduceShader.LoadSource();

        Assert.Equal(ZoneReduceShader.ThreadsPerZone, ReadDefine(source, "THREADS_PER_ZONE"));
        Assert.Equal(ZoneReduceShader.ThreadsPerLine, ReadDefine(source, "THREADS_PER_LINE"));
        Assert.Equal(ZoneSampleFrame.ProfileResolution, ReadDefine(source, "PROFILE_RESOLUTION"));
    }

    [Theory]
    [InlineData("ZoneReduce.hlsl", "line")]
    [InlineData("ZoneReduce.hlsl", "point")]
    [InlineData("ZoneReduce.hlsl", "triangle")]
    [InlineData("ZoneReduce.hlsl", "sample")]
    public void Shader_DoesNotUseFxcKeywordsAsIdentifiers(string file, string keyword)
    {
        var declaration = new Regex($@"\b(?:float\d?|uint\d?|int\d?|bool)\s+{keyword}\b");

        Assert.False(declaration.IsMatch(ZoneReduceShader.LoadSource()), $"{file} declares a variable named '{keyword}', an FXC keyword.");
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
    public void ZoneReduceConstants_FieldOrderAndSize_MatchShader()
    {
        Assert.Equal(CSharpFields<ZoneReduceConstants>(), ShaderFields("ZoneReduceConstants"));
        Assert.Equal(ZoneReduceConstants.SizeInBytes, Unsafe.SizeOf<ZoneReduceConstants>());
        Assert.Equal(0, ZoneReduceConstants.SizeInBytes % 16);
    }

    [Fact]
    public void EdgeProfileConstants_FieldOrderAndSize_MatchShader()
    {
        Assert.Equal(CSharpFields<EdgeProfileConstants>(), ShaderFields("EdgeProfileConstants"));
        Assert.Equal(EdgeProfileConstants.SizeInBytes, Unsafe.SizeOf<EdgeProfileConstants>());
        Assert.Equal(0, EdgeProfileConstants.SizeInBytes % 16);
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

    private static string?[] CSharpFields<T>() =>
        typeof(T).GetConstructors().Single(constructor => constructor.GetParameters().Length > 1)
            .GetParameters().Select(parameter => parameter.Name).ToArray();

    private static string[] ShaderFields(string cbufferName)
    {
        var body = Regex.Match(ZoneReduceShader.LoadSource(), $@"cbuffer\s+{cbufferName}[^{{]*\{{(?<body>[^}}]*)\}}");
        Assert.True(body.Success, $"cbuffer {cbufferName} not found.");
        return FieldPattern().Matches(body.Groups["body"].Value).Select(match => match.Groups["name"].Value).ToArray();
    }

    private static int ReadDefine(string source, string name)
    {
        var match = Regex.Match(source, $@"#define\s+{name}\s+(\d+)");
        Assert.True(match.Success, $"#define {name} not found.");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\s*(?:uint|float|float2|float4)\s+(?<name>\w+)\s*;", RegexOptions.Multiline)]
    private static partial Regex FieldPattern();
}
