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
        string adapterName,
        IDXGIOutput1 output1,
        IDXGIOutput5? output5,
        IDXGIOutput6? output6,
        ID3D11Device device,
        ID3D11DeviceContext context)
    {
        RequestedOutputName = requestedOutputName;
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
    /// a device on its adapter.
    /// </summary>
    /// <exception cref="CaptureException">The monitor does not exist or the device cannot be created.</exception>
    public static CaptureDevice Create(string? requestedOutputName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var (adapter, output) = FindOutput(factory, requestedOutputName);

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
            return new CaptureDevice(
                requestedOutputName,
                adapter.Description1.Description,
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

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output) FindOutput(IDXGIFactory1 factory, string? requestedOutputName)
    {
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
            {
                var description = output.Description;
                if (description.AttachedToDesktop && IsMatch(description, requestedOutputName))
                {
                    return (adapter, output);
                }

                output.Dispose();
            }

            adapter.Dispose();
        }

        var target = requestedOutputName ?? "the primary monitor";
        throw new CaptureException($"No desktop-attached output matches {target}.", CaptureRecovery.RecreateDevice);
    }

    private static bool IsMatch(OutputDescription description, string? requestedOutputName) =>
        requestedOutputName is null
            ? description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0
            : string.Equals(description.DeviceName, requestedOutputName, StringComparison.OrdinalIgnoreCase);

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
