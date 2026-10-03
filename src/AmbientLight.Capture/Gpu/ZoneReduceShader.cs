using System.Reflection;
using SharpGen.Runtime;
using Vortice.D3DCompiler;

namespace AmbientLight.Capture.Gpu;

/// <summary>Loads ZoneReduce.hlsl from the assembly and compiles it once per process.</summary>
/// <remarks>
/// Runtime compilation uses d3dcompiler_47.dll, which ships with every supported Windows version.
/// It costs a few tens of milliseconds once at startup and keeps the HLSL as the single source of truth.
/// </remarks>
internal static class ZoneReduceShader
{
    public const string ResourceName = "AmbientLight.Capture.Shaders.ZoneReduce.hlsl";
    public const string EntryPoint = "CSMain";
    public const string Profile = "cs_5_0";

    /// <summary>Thread-group width declared by <c>THREADS_PER_ZONE</c> in the shader.</summary>
    public const int ThreadsPerZone = 64;

    private static readonly Lazy<byte[]> CompiledBytecode = new(Compile, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Compiled cs_5_0 bytecode.</summary>
    public static ReadOnlySpan<byte> Bytecode => CompiledBytecode.Value;

    /// <summary>Reads the HLSL source embedded in this assembly.</summary>
    public static string LoadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] Compile()
    {
        var source = LoadSource();

        // Warnings are deliberately not errors here: FXC emits advisory warnings (for example X3571 on
        // pow() when it cannot prove the base is positive) and an advisory must never stop the app on a
        // user's machine. The shader is held warning-free at development time (see docs/ARCHITECTURE.md).
        var flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness;
        try
        {
            return Compiler.Compile(source, EntryPoint, "ZoneReduce.hlsl", Profile, flags).ToArray();
        }
        catch (SharpGenException exception)
        {
            throw new InvalidOperationException($"ZoneReduce.hlsl failed to compile ({Profile}): {exception.Message}", exception);
        }
    }
}
