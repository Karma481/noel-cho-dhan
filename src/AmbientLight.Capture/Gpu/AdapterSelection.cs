namespace AmbientLight.Capture.Gpu;

/// <summary>One (adapter, output) pair as DXGI enumerates it.</summary>
/// <param name="AdapterLuid">Locally unique id of the adapter (stable while the machine runs).</param>
/// <param name="AdapterIsSoftware">Microsoft Basic Render Driver and other software adapters.</param>
/// <param name="DeviceName">GDI name of the output, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="AttachedToDesktop">The output is part of the desktop.</param>
/// <param name="AtDesktopOrigin">The output's top-left corner is (0,0): the primary monitor.</param>
public readonly record struct OutputCandidate(
    long AdapterLuid,
    bool AdapterIsSoftware,
    string DeviceName,
    bool AttachedToDesktop,
    bool AtDesktopOrigin);

/// <summary>Result of <see cref="AdapterSelection.Select"/>.</summary>
public enum AdapterSelectionOutcome
{
    /// <summary>A usable candidate was found.</summary>
    Found = 0,

    /// <summary>No attached output matches the requested monitor (unplugged, wrong name).</summary>
    NoMatchingOutput = 1,

    /// <summary>The monitor exists, but every adapter exposing it already failed Desktop Duplication.</summary>
    AllAdaptersExcluded = 2,
}

/// <summary>
/// Chooses which adapter to create the capture device on.
/// </summary>
/// <remarks>
/// <para>
/// On a hybrid laptop (integrated + discrete GPU) the built-in panel is wired to the integrated GPU, and
/// Desktop Duplication only works from a device on that adapter: from the discrete one it fails with
/// DXGI_ERROR_UNSUPPORTED. Depending on the Windows version and the per-app GPU preference, DXGI may list the
/// panel's output under either adapter. The selection therefore:
/// </para>
/// <list type="number">
/// <item>walks candidates in <b>minimum-power order</b> (<c>IDXGIFactory6::EnumAdapterByGpuPreference</c>),
/// so the integrated GPU is tried first;</item>
/// <item>skips software adapters, which can never duplicate a physical output;</item>
/// <item>skips adapters on which duplication already failed with UNSUPPORTED in this session, so the next
/// attempt lands on another GPU instead of failing the same way forever.</item>
/// </list>
/// </remarks>
public static class AdapterSelection
{
    /// <summary>
    /// Returns the index into <paramref name="candidates"/> (already in preference order) of the first usable
    /// match for <paramref name="requestedOutputName"/> (null = primary monitor), or -1 with the reason.
    /// </summary>
    public static (int Index, AdapterSelectionOutcome Outcome) Select(
        IReadOnlyList<OutputCandidate> candidates,
        string? requestedOutputName,
        IReadOnlySet<long> excludedAdapters)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(excludedAdapters);

        var anyMatch = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.AdapterIsSoftware || !candidate.AttachedToDesktop || !IsMatch(candidate, requestedOutputName))
            {
                continue;
            }

            anyMatch = true;
            if (!excludedAdapters.Contains(candidate.AdapterLuid))
            {
                return (i, AdapterSelectionOutcome.Found);
            }
        }

        return (-1, anyMatch ? AdapterSelectionOutcome.AllAdaptersExcluded : AdapterSelectionOutcome.NoMatchingOutput);
    }

    private static bool IsMatch(OutputCandidate candidate, string? requestedOutputName) =>
        requestedOutputName is null
            ? candidate.AtDesktopOrigin
            : string.Equals(candidate.DeviceName, requestedOutputName, StringComparison.OrdinalIgnoreCase);
}
