using System.Diagnostics;

namespace AmbientLight.Core.Frames;

/// <summary>
/// Timestamps carried with every frame, all in <see cref="Stopwatch"/> ticks.
/// On Windows Stopwatch ticks are QueryPerformanceCounter ticks, the same clock DXGI reports in
/// <c>DXGI_OUTDUPL_FRAME_INFO.LastPresentTime</c>, so end-to-end latency is a plain subtraction.
/// </summary>
/// <param name="SourcePresent">When DWM presented the captured desktop image; 0 when unknown.</param>
/// <param name="Captured">When the GPU reduction result became available on the CPU.</param>
/// <param name="Processed">When the processing stage finished the frame; 0 before processing.</param>
public readonly record struct FrameTiming(long SourcePresent, long Captured, long Processed)
{
    /// <summary>Elapsed time from <paramref name="startTicks"/> to <paramref name="endTicks"/>; zero if either is unknown.</summary>
    public static TimeSpan Between(long startTicks, long endTicks) =>
        startTicks == 0 || endTicks == 0 || endTicks < startTicks
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(startTicks, endTicks);

    /// <summary>Latency from desktop present to the end of processing.</summary>
    public TimeSpan PresentToProcessed => Between(SourcePresent, Processed);

    /// <summary>Latency from desktop present to <paramref name="nowTicks"/> (for example the end of a serial write).</summary>
    public TimeSpan PresentTo(long nowTicks) => Between(SourcePresent, nowTicks);
}
