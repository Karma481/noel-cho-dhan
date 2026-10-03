using AmbientLight.Capture.ColorSpace;
using AmbientLight.Capture.Gpu;
using AmbientLight.Capture.Recovery;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DxgiResult = Vortice.DXGI.ResultCode;

namespace AmbientLight.Capture.Duplication;

/// <summary>
/// Owns an <see cref="IDXGIOutputDuplication"/> and the frame it currently holds.
/// </summary>
/// <remarks>
/// <para>
/// Frame ownership follows Microsoft's guidance for <c>AcquireNextFrame</c>: the frame is released
/// immediately before the next acquire, not right after use. While the frame is held, DWM keeps
/// accumulating updates for the next one, and the GPU work that reads the surface is guaranteed to
/// have completed (the reducer waits on its fence) before the surface goes back to the OS.
/// </para>
/// <para>
/// The duplication surface is sampled in place when the OS created it with
/// <c>D3D11_BIND_SHADER_RESOURCE</c>. Otherwise it is first copied into a private texture of the same
/// size and format; that copy is a GPU-to-GPU blit, never a CPU readback.
/// </para>
/// </remarks>
internal sealed class DesktopDuplicator : IDisposable
{
    private readonly CaptureDevice _device;

    private IDXGIOutputDuplication? _duplication;
    private IDXGIResource? _heldResource;
    private ID3D11Texture2D? _heldTexture;
    private bool _frameHeld;

    private ID3D11Texture2D? _copyTexture;
    private ID3D11ShaderResourceView? _view;
    private IntPtr _viewSourcePointer;

    public DesktopDuplicator(CaptureDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>True while a duplication interface exists.</summary>
    public bool IsActive => _duplication is not null;

    /// <summary>True when the OS refused <c>DuplicateOutput1</c> and the SDR-only legacy API is in use.</summary>
    public bool UsesLegacyApi { get; private set; }

    /// <summary>Description of the active duplication (surface mode and rotation).</summary>
    public OutduplDescription Description { get; private set; }

    /// <summary>
    /// Creates the duplication. Re-reads the output first, because the access-lost that precedes a
    /// recreation is usually caused by an HDR toggle, resolution change or rotation.
    /// </summary>
    /// <exception cref="CaptureException">Duplication is not possible right now; the recovery says what to do.</exception>
    public void Start()
    {
        Stop();

        var output = _device.RefreshOutputInfo();
        _duplication = CreateDuplication(output.IsHdr);
        Description = _duplication.Description;
    }

    /// <summary>Releases any held frame and destroys the duplication interface.</summary>
    public void Stop()
    {
        if (_duplication is null)
        {
            return;
        }

        try
        {
            ReleaseHeldFrame();
        }
        catch (CaptureException)
        {
            // The duplication is being discarded anyway; a failed release (typically access lost) is moot.
            DisposeHeldFrame();
        }

        DisposeView();
        _duplication.Dispose();
        _duplication = null;
    }

    /// <summary>
    /// Releases the previously held frame, then waits up to <paramref name="timeoutMs"/> for the next one.
    /// </summary>
    /// <exception cref="CaptureException">Access lost, device removed or another failure, with its recovery.</exception>
    public AcquireOutcome TryAcquire(int timeoutMs, out AcquiredFrame frame)
    {
        var duplication = _duplication ?? throw new InvalidOperationException("Duplication has not been started.");
        ReleaseHeldFrame();

        var result = duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (result.Code == DxgiResult.WaitTimeout.Code)
        {
            resource?.Dispose();
            frame = default;
            return AcquireOutcome.Timeout;
        }

        if (result.Failure || resource is null)
        {
            resource?.Dispose();
            throw new CaptureException($"AcquireNextFrame failed: {result}", DxgiErrorClassifier.Classify(result.Code));
        }

        _heldResource = resource;
        _frameHeld = true;
        frame = AcquiredFrame.From(info);
        return AcquireOutcome.Acquired;
    }

    /// <summary>
    /// Returns a shader resource view of the held frame and its texture description. The view stays
    /// valid until the next <see cref="TryAcquire"/>.
    /// </summary>
    public ID3D11ShaderResourceView GetSampleableView(out Texture2DDescription description)
    {
        if (!_frameHeld || _heldResource is null)
        {
            throw new InvalidOperationException("No frame is held.");
        }

        _heldTexture ??= _heldResource.QueryInterface<ID3D11Texture2D>();
        description = _heldTexture.Description;

        // Fail early with a clear message instead of letting the shader misinterpret an unknown format.
        _ = ColorSpaceMapping.EncodingOf(description.Format);

        if ((description.BindFlags & BindFlags.ShaderResource) != 0)
        {
            return GetDirectView(_heldTexture);
        }

        return GetCopyView(_heldTexture, description);
    }

    public void Dispose()
    {
        Stop();
        DisposeView();
    }

    private IDXGIOutputDuplication CreateDuplication(bool isHdr)
    {
        var device = _device.Device;

        if (_device.Output5 is not null)
        {
            try
            {
                var duplication = _device.Output5.DuplicateOutput1(device, ColorSpaceMapping.PreferredDuplicationFormats(isHdr));
                UsesLegacyApi = false;
                return duplication;
            }
            catch (SharpGenException exception) when (
                exception.ResultCode.Code == DxgiResult.Unsupported.Code ||
                exception.ResultCode.Code == Result.InvalidArg.Code)
            {
                // DuplicateOutput1 requires per-monitor-v2 DPI awareness and a format the driver accepts.
                // The legacy API has neither requirement; HDR desktops are then delivered tone-mapped to BGRA8 by DWM.
                UsesLegacyApi = true;
            }
            catch (SharpGenException exception)
            {
                throw new CaptureException($"DuplicateOutput1 failed: {exception.ResultCode}", exception);
            }
        }

        try
        {
            UsesLegacyApi = true;
            return _device.Output1.DuplicateOutput(device);
        }
        catch (SharpGenException exception)
        {
            throw new CaptureException($"DuplicateOutput failed: {exception.ResultCode}", exception);
        }
    }

    private ID3D11ShaderResourceView GetDirectView(ID3D11Texture2D texture)
    {
        if (_view is not null && _copyTexture is null && _viewSourcePointer == texture.NativePointer)
        {
            return _view;
        }

        DisposeView();
        _view = _device.Device.CreateShaderResourceView(texture);
        _viewSourcePointer = texture.NativePointer;
        return _view;
    }

    private ID3D11ShaderResourceView GetCopyView(ID3D11Texture2D source, Texture2DDescription sourceDescription)
    {
        if (_copyTexture is not null)
        {
            var existing = _copyTexture.Description;
            if (existing.Width != sourceDescription.Width ||
                existing.Height != sourceDescription.Height ||
                existing.Format != sourceDescription.Format)
            {
                DisposeView();
            }
        }

        if (_copyTexture is null)
        {
            var copyDescription = new Texture2DDescription
            {
                Width = sourceDescription.Width,
                Height = sourceDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            _copyTexture = _device.Device.CreateTexture2D(copyDescription);
            _view = _device.Device.CreateShaderResourceView(_copyTexture);
            _viewSourcePointer = IntPtr.Zero;
        }

        _device.Context.CopyResource(_copyTexture, source);
        return _view!;
    }

    private void ReleaseHeldFrame()
    {
        if (!_frameHeld)
        {
            return;
        }

        DisposeHeldFrame();
        var result = _duplication!.ReleaseFrame();

        // INVALID_CALL means the frame was already released by the OS (for example after access lost).
        if (result.Failure && result.Code != DxgiResult.InvalidCall.Code)
        {
            throw new CaptureException($"ReleaseFrame failed: {result}", DxgiErrorClassifier.Classify(result.Code));
        }
    }

    private void DisposeHeldFrame()
    {
        _heldTexture?.Dispose();
        _heldTexture = null;
        _heldResource?.Dispose();
        _heldResource = null;
        _frameHeld = false;
    }

    private void DisposeView()
    {
        _view?.Dispose();
        _view = null;
        _copyTexture?.Dispose();
        _copyTexture = null;
        _viewSourcePointer = IntPtr.Zero;
    }
}
