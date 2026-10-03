using System.Reflection;
using SharpGen.Runtime;
using Vortice.D3DCompiler;

namespace AmbientLight.Capture.Gpu;

/// <summary>Loads ZoneReduce.hlsl from the assembly and compiles its kernels once per process.</summary>
/// <remarks>
/// Runtime compilation uses d3dcompiler_47.dll (FXC), which ships with every supported Windows version.
/// It costs a few tens of milliseconds once at startup and keeps the HLSL as the single source of truth.
/// The same compiler and profile are exercised at development time by tools/validate-shaders.sh.
/// </remarks>
internal static class ZoneReduceShader
{
    public const string ResourceName = "AmbientLight.Capture.Shaders.ZoneReduce.hlsl";
    public const string ZoneReduceEntryPoint = "CSMain";
    public const string EdgeProfileEntryPoint = "CSEdgeProfile";
    public const string Profile = "cs_5_0";

    /// <summary>Thread-group width declared by <c>THREADS_PER_ZONE</c> in the shader.</summary>
    public const int ThreadsPerZone = 64;

    /// <summary>Thread-group width declared by <c>THREADS_PER_LINE</c> in the shader.</summary>
    public const int ThreadsPerLine = 128;

    private static readonly Lazy<byte[]> ZoneReduceBytecode =
        new(() => Compile(ZoneReduceEntryPoint), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> EdgeProfileBytecode =
        new(() => Compile(EdgeProfileEntryPoint), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Compiled cs_5_0 bytecode of the zone reduction kernel.</summary>
    public static ReadOnlySpan<byte> ZoneReduce => ZoneReduceBytecode.Value;

    /// <summary>Compiled cs_5_0 bytecode of the letterbox luminance-profile kernel.</summary>
    public static ReadOnlySpan<byte> EdgeProfile => EdgeProfileBytecode.Value;

    /// <summary>Reads the HLSL source embedded in this assembly.</summary>
    public static string LoadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] Compile(string entryPoint)
    {
        var source = LoadSource();

        // Warnings are deliberately not errors here: an advisory diagnostic must never stop the app on a
        // user's machine. The shader is held warning-free with /WX at development time instead.
        var flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness;
        try
        {
            return Compiler.Compile(source, entryPoint, "ZoneReduce.hlsl", Profile, flags).ToArray();
        }
        catch (SharpGenException exception)
        {
            throw new InvalidOperationException($"ZoneReduce.hlsl:{entryPoint} failed to compile ({Profile}): {exception.Message}", exception);
        }
    }
}
