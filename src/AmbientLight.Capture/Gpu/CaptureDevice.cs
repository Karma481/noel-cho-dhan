using System.Diagnostics.CodeAnalysis;
using AmbientLight.Capture.ColorSpace;
using AmbientLight.Capture.Interop;
using AmbientLight.Capture.Recovery;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AmbientLight.Capture.Gpu;

/// <summary>
/// The D3D11 device created on the GPU that drives the captured monitor, plus that monitor's DXGI output.
/// Owned and used exclusively by the capture thread.
/// </summary>
/// <remarks>
/// Desktop Duplication only works with a device created on the adapter the output is attached to. On
/// hybrid laptops (iGPU drives the panel, dGPU renders games) picking the "fastest" adapter instead
/// fails with DXGI_ERROR_UNSUPPORTED, so the adapter is always taken from the matched output.
/// </remarks>
internal sealed class CaptureDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    private readonly string _adapterName;

    private CaptureDevice(
        string? requestedOutputName,
        long adapterLuid,
        string adapterName,
        IDXGIOutput1 output1,
        IDXGIOutput5? output5,
        IDXGIOutput6? output6,
        ID3D11Device device,
        ID3D11DeviceContext context)
    {
        RequestedOutputName = requestedOutputName;
        AdapterLuid = adapterLuid;
        _adapterName = adapterName;
        Output1 = output1;
        Output5 = output5;
        Output6 = output6;
        Device = device;
        Context = context;
        Device5 = device.QueryInterfaceOrNull<ID3D11Device5>();
        Context4 = context.QueryInterfaceOrNull<ID3D11DeviceContext4>();
        OutputInfo = ReadOutputInfo();
    }

    /// <summary>The <c>CaptureSettings.OutputDeviceName</c> this device was created for (null = primary).</summary>
    public string? RequestedOutputName { get; }

    /// <summary>LUID of the adapter the device lives on, used to exclude it after DXGI_ERROR_UNSUPPORTED.</summary>
    public long AdapterLuid { get; }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>Available on Windows 10 1703+ with a WDDM 2.2+ driver; enables fence-based readback.</summary>
    public ID3D11Device5? Device5 { get; }

    public ID3D11DeviceContext4? Context4 { get; }

    public IDXGIOutput1 Output1 { get; }

    /// <summary>Available on Windows 10 1703+; enables <c>DuplicateOutput1</c> (FP16 HDR capture).</summary>
    public IDXGIOutput5? Output5 { get; }

    /// <summary>Available on Windows 10 1703+; reports color space and luminance.</summary>
    public IDXGIOutput6? Output6 { get; }

    /// <summary>Latest description of the captured monitor.</summary>
    public DisplayOutputInfo OutputInfo { get; private set; }

    /// <summary>
    /// Finds the requested monitor (GDI name, or the primary monitor when <see langword="null"/>) and creates
    /// a device on the first suitable adapter exposing it, in minimum-power order, skipping
    /// <paramref name="excludedAdapters"/> (see <see cref="AdapterSelection"/>).
    /// </summary>
    /// <exception cref="CaptureException">The monitor does not exist or the device cannot be created.</exception>
    public static CaptureDevice Create(string? requestedOutputName, IReadOnlySet<long> excludedAdapters)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var (adapter, output) = FindOutput(factory, requestedOutputName, excludedAdapters);

        using (adapter)
        using (output)
        {
            var result = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.None,
                FeatureLevels,
                out ID3D11Device? device,
                out ID3D11DeviceContext? context);

            if (result.Failure || device is null || context is null)
            {
                device?.Dispose();
                context?.Dispose();
                throw new CaptureException(
                    $"D3D11CreateDevice failed on '{adapter.Description1.Description}': {result}",
                    DxgiErrorClassifier.Classify(result.Code));
            }

            var output1 = output.QueryInterface<IDXGIOutput1>();
            var description = adapter.Description1;
            return new CaptureDevice(
                requestedOutputName,
                ToInt64(description.Luid),
                description.Description,
                output1,
                output.QueryInterfaceOrNull<IDXGIOutput5>(),
                output.QueryInterfaceOrNull<IDXGIOutput6>(),
                device,
                context);
        }
    }

    /// <summary>Re-reads rotation, HDR state, peak luminance and SDR white level.</summary>
    public DisplayOutputInfo RefreshOutputInfo()
    {
        OutputInfo = ReadOutputInfo();
        return OutputInfo;
    }

    /// <summary>Throws a <see cref="CaptureException"/> requesting device recreation if the device was removed.</summary>
    public void ThrowIfDeviceRemoved()
    {
        var reason = Device.DeviceRemovedReason;
        if (reason.Failure)
        {
            throw new CaptureException($"The D3D11 device was removed: {reason}", CaptureRecovery.RecreateDevice);
        }
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context4?.Dispose();
        Device5?.Dispose();
        Context.Dispose();
        Device.Dispose();
        Output6?.Dispose();
        Output5?.Dispose();
        Output1.Dispose();
    }

    internal static long ToInt64(Vortice.Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output) FindOutput(
        IDXGIFactory1 factory,
        string? requestedOutputName,
        IReadOnlySet<long> excludedAdapters)
    {
        var adapters = new List<IDXGIAdapter1>();
        var outputs = new List<(int AdapterIndex, IDXGIOutput Output)>();
        var candidates = new List<OutputCandidate>();
        try
        {
            using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
            for (uint index = 0; TryEnumAdapter(factory, factory6, index, out var adapter); index++)
            {
                adapters.Add(adapter);
                var description = adapter.Description1;
                var isSoftware = (description.Flags & AdapterFlags.Software) != 0;
                for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                {
                    var outputDescription = output.Description;
                    outputs.Add((adapters.Count - 1, output));
                    candidates.Add(new OutputCandidate(
                        ToInt64(description.Luid),
                        isSoftware,
                        outputDescription.DeviceName,
                        outputDescription.AttachedToDesktop,
                        outputDescription.DesktopCoordinates.Left == 0 && outputDescription.DesktopCoordinates.Top == 0));
                }
            }

            var (selected, outcome) = AdapterSelection.Select(candidates, requestedOutputName, excludedAdapters);
            var target = requestedOutputName ?? "the primary monitor";
            switch (outcome)
            {
                case AdapterSelectionOutcome.NoMatchingOutput:
                    throw new CaptureException($"No desktop-attached output matches {target}.", CaptureRecovery.RecreateDevice);
                case AdapterSelectionOutcome.AllAdaptersExcluded:
                    throw new CaptureException(
                        $"Desktop Duplication is unsupported on every GPU exposing {target}. On a hybrid laptop, set " +
                        "AmbientLight to 'Power saving' in Windows Settings > System > Display > Graphics.",
                        CaptureRecovery.Fatal);
            }

            var (adapterIndex, selectedOutput) = outputs[selected];
            var selectedAdapter = adapters[adapterIndex];

            // Hand the chosen pair to the caller; everything else is released in the finally block.
            outputs.RemoveAt(selected);
            adapters.RemoveAt(adapterIndex);
            return (selectedAdapter, selectedOutput);
        }
        finally
        {
            foreach (var (_, output) in outputs)
            {
                output.Dispose();
            }

            foreach (var adapter in adapters)
            {
                adapter.Dispose();
            }
        }
    }

    /// <summary>Enumerates adapters integrated-GPU first when DXGI 1.6 is available, else in default order.</summary>
    private static bool TryEnumAdapter(
        IDXGIFactory1 factory,
        IDXGIFactory6? factory6,
        uint index,
        [NotNullWhen(true)] out IDXGIAdapter1? adapter)
    {
        var result = factory6 is not null
            ? factory6.EnumAdapterByGpuPreference(index, GpuPreference.MinimumPower, out adapter)
            : factory.EnumAdapters1(index, out adapter);
        return result.Success && adapter is not null;
    }

    private DisplayOutputInfo ReadOutputInfo()
    {
        var description = Output1.Description;
        var bounds = description.DesktopCoordinates;
        var colorSpace = ColorSpaceType.RgbFullG22NoneP709;
        var maxLuminance = 0f;

        if (Output6 is not null)
        {
            try
            {
                var description1 = Output6.Description1;
                colorSpace = description1.ColorSpace;
                maxLuminance = description1.MaxLuminance;
            }
            catch (SharpGenException)
            {
                // GetDesc1 can fail transiently during a mode change; treat the output as SDR until the
                // access-lost that follows the mode change triggers another refresh.
                colorSpace = ColorSpaceType.RgbFullG22NoneP709;
            }
        }

        var sdrWhiteNits = DisplayConfigInterop.TryGetSdrWhiteLevelNits(description.DeviceName) ?? 0f;

        return new DisplayOutputInfo(
            description.DeviceName,
            _adapterName,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            description.Rotation,
            colorSpace,
            maxLuminance,
            sdrWhiteNits);
    }
}
