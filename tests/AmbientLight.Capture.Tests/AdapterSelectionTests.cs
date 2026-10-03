using AmbientLight.Capture.Gpu;

namespace AmbientLight.Capture.Tests;

public sealed class AdapterSelectionTests
{
    private const long IntegratedGpu = 0x1_0000_0001;
    private const long DiscreteGpu = 0x1_0000_0002;
    private const long BasicRenderDriver = 0x1_0000_0003;

    // A hybrid laptop as DXGI lists it in minimum-power order: the panel appears under both GPUs.
    private static readonly OutputCandidate[] HybridLaptop =
    [
        new(IntegratedGpu, false, @"\\.\DISPLAY1", AttachedToDesktop: true, AtDesktopOrigin: true),
        new(DiscreteGpu, false, @"\\.\DISPLAY1", AttachedToDesktop: true, AtDesktopOrigin: true),
        new(DiscreteGpu, false, @"\\.\DISPLAY2", AttachedToDesktop: true, AtDesktopOrigin: false),
    ];

    [Fact]
    public void PrimaryMonitor_PrefersTheFirstAdapterInPowerOrder()
    {
        var (index, outcome) = AdapterSelection.Select(HybridLaptop, null, new HashSet<long>());

        Assert.Equal(AdapterSelectionOutcome.Found, outcome);
        Assert.Equal(0, index);
    }

    [Fact]
    public void AdapterThatFailedDuplication_IsSkipped()
    {
        var (index, outcome) = AdapterSelection.Select(HybridLaptop, null, new HashSet<long> { IntegratedGpu });

        Assert.Equal(AdapterSelectionOutcome.Found, outcome);
        Assert.Equal(1, index);
    }

    [Fact]
    public void EveryAdapterExcluded_IsReportedDistinctlyFromAMissingMonitor()
    {
        var (index, outcome) = AdapterSelection.Select(HybridLaptop, null, new HashSet<long> { IntegratedGpu, DiscreteGpu });

        Assert.Equal(AdapterSelectionOutcome.AllAdaptersExcluded, outcome);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void NamedMonitor_IsMatchedIgnoringCase()
    {
        var (index, outcome) = AdapterSelection.Select(HybridLaptop, @"\\.\display2", new HashSet<long>());

        Assert.Equal(AdapterSelectionOutcome.Found, outcome);
        Assert.Equal(2, index);
    }

    [Fact]
    public void UnknownMonitor_IsNoMatchingOutput()
    {
        var (index, outcome) = AdapterSelection.Select(HybridLaptop, @"\\.\DISPLAY9", new HashSet<long>());

        Assert.Equal(AdapterSelectionOutcome.NoMatchingOutput, outcome);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void SoftwareAdaptersAndDetachedOutputs_AreNeverChosen()
    {
        OutputCandidate[] candidates =
        [
            new(BasicRenderDriver, true, @"\\.\DISPLAY1", true, true),
            new(IntegratedGpu, false, @"\\.\DISPLAY1", AttachedToDesktop: false, AtDesktopOrigin: true),
            new(DiscreteGpu, false, @"\\.\DISPLAY1", true, true),
        ];

        var (index, outcome) = AdapterSelection.Select(candidates, null, new HashSet<long>());

        Assert.Equal(AdapterSelectionOutcome.Found, outcome);
        Assert.Equal(2, index);
    }

    [Fact]
    public void OnlySoftwareAdapters_MeansNoMatchingOutput()
    {
        OutputCandidate[] candidates = [new(BasicRenderDriver, true, @"\\.\DISPLAY1", true, true)];

        Assert.Equal(AdapterSelectionOutcome.NoMatchingOutput, AdapterSelection.Select(candidates, null, new HashSet<long>()).Outcome);
    }
}
