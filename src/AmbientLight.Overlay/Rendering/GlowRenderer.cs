using System.Diagnostics;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using CompositionBorderMode = Vortice.DirectComposition.BorderMode;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DBorderMode = Vortice.Direct2D1.BorderMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using DxgiResult = Vortice.DXGI.ResultCode;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using CompositionInterpolationMode = Vortice.DirectComposition.BitmapInterpolationMode;
using D2DCompositeMode = Vortice.Direct2D1.CompositeMode;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace AmbientLight.Overlay.Rendering;

/// <summary>The rendering device was lost (driver reset, TDR, GPU removed); recreate the renderer.</summary>
public sealed class OverlayDeviceLostException : Exception
{
    /// <summary>Creates the exception with a generic message.</summary>
    public OverlayDeviceLostException()
        : base("The overlay rendering device was lost.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public OverlayDeviceLostException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the failure that revealed the loss.</summary>
    public OverlayDeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Draws the glow into a DirectComposition swapchain attached to the overlay window.
/// </summary>
/// <remarks>
/// <para>Per redraw, all on this renderer's own D3D11 device (never shared with the capture thread):</para>
/// <code>
/// canvas (render size + margin)  ◄─ FillRectangle per zone band (aliased, so adjacent bands have no seams)
///     │
///     ▼ Direct2D Gaussian blur effect (border mode hard)
/// swapchain back buffer (render size = screen / divisor)  ◄─ DrawImage at -margin
///     │ Present(1)
///     ▼
/// DirectComposition visual: scale × divisor, bilinear ──► DWM composes it over the monitor
/// </code>
/// <para>
/// Rendering at 1/8 of the screen resolution makes the blur 64 times cheaper; the glow is a
/// low-frequency signal, so the bilinear upscale done by DWM is visually indistinguishable from a
/// full-resolution render. No GPU object is created per frame: the canvas, brush, blur effect and its
/// output are created when the layout changes and reused.
/// </para>
/// </remarks>
internal sealed class GlowRenderer : IDisposable
{
    private const int D2DErrorRecreateTarget = unchecked((int)0x8899000C);

    /// <summary>Upper bound of the D2D Gaussian blur standard deviation (D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION).</summary>
    private const float MaxBlurSigma = 250f;

    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private static readonly D2DPixelFormat PremultipliedBgra = new(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied);
    private static readonly Color4 Transparent = new(0f, 0f, 0f, 0f);

    private readonly ID3D11Device _device;
    private readonly IDXGIDevice1 _dxgiDevice;
    private readonly IDXGIFactory2 _dxgiFactory;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly GaussianBlur _blur;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _visual;

    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _backBuffer;
    private ID2D1Bitmap1? _canvas;
    private ID2D1Image? _blurOutput;
    private GlowLayout _layout;
    private bool _commitPending;

    private GlowRenderer(IntPtr hwnd, ID3D11Device device)
    {
        _device = device;
        try
        {
            _dxgiDevice = device.QueryInterface<IDXGIDevice1>();

            // At most one queued frame: a new glow color reaches the screen on the next DWM composition.
            _dxgiDevice.MaximumFrameLatency = 1;

            using (var adapter = _dxgiDevice.GetAdapter())
            {
                _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
            }

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
            _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
            _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _context.UnitMode = UnitMode.Pixels;
            _context.AntialiasMode = AntialiasMode.Aliased;
            _brush = _context.CreateSolidColorBrush(Transparent);
            _blur = new GaussianBlur(_context)
            {
                BorderMode = D2DBorderMode.Hard,
                Optimization = GaussianBlurOptimization.Balanced,
            };

            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
            _compositionDevice.CreateTargetForHwnd(hwnd, true, out var target).CheckError();
            _compositionTarget = target!;
            _visual = _compositionDevice.CreateVisual();
            _visual.SetBitmapInterpolationMode(CompositionInterpolationMode.Linear).CheckError();
            _visual.SetBorderMode(CompositionBorderMode.Hard).CheckError();
            _compositionTarget.SetRoot(_visual).CheckError();
        }
        catch
        {
            // The caller owns (and disposes) the D3D device; release everything created after it.
            ReleaseInterfaces(disposeDevice: false);
            throw;
        }
    }

    /// <summary>Description of the GPU in use, for diagnostics.</summary>
    public string AdapterName { get; private set; } = string.Empty;

    /// <summary>
    /// Creates the renderer for <paramref name="hwnd"/>, with its D3D11 device on the adapter driving the
    /// monitor <paramref name="monitorDeviceName"/>, so DWM never has to copy the glow across GPUs.
    /// </summary>
    /// <exception cref="OverlayDeviceLostException">No usable GPU right now (retry later).</exception>
    public static GlowRenderer Create(IntPtr hwnd, string monitorDeviceName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = FindAdapter(factory, monitorDeviceName);

        var result = D3D11.D3D11CreateDevice(
            adapter,
            adapter is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels,
            out ID3D11Device? device);
        if (result.Failure || device is null)
        {
            device?.Dispose();
            throw new OverlayDeviceLostException($"D3D11CreateDevice failed for the overlay: {result}");
        }

        try
        {
            return new GlowRenderer(hwnd, device)
            {
                AdapterName = adapter?.Description1.Description ?? "default hardware adapter",
            };
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies a layout: (re)creates the swapchain when the render size changes, the canvas when the margin
    /// changes, updates the blur strength and the composition scale, and commits the visual tree.
    /// </summary>
    public void Configure(in GlowLayout layout)
    {
        try
        {
            if (_swapChain is null || layout.RenderWidth != _layout.RenderWidth || layout.RenderHeight != _layout.RenderHeight)
            {
                CreateSwapChain(layout.RenderWidth, layout.RenderHeight);
            }

            if (_canvas is null || layout.CanvasWidth != _layout.CanvasWidth || layout.CanvasHeight != _layout.CanvasHeight)
            {
                CreateCanvas(layout.CanvasWidth, layout.CanvasHeight);
            }

            _blur.StandardDeviation = Math.Clamp(layout.BlurSigma, 0f, MaxBlurSigma);
            _visual.SetTransform(Matrix3x2.CreateScale(layout.ScaleX, layout.ScaleY)).CheckError();

            // Committed after the next Present, so DWM never composes a new swapchain before it has content.
            _commitPending = true;
            _layout = layout;
        }
        catch (SharpGenException exception) when (IsDeviceLoss(exception.ResultCode.Code))
        {
            throw new OverlayDeviceLostException("The overlay device was lost while configuring.", exception);
        }
    }

    /// <summary>Draws <paramref name="segments"/> (canvas coordinates), blurs them and presents. Returns the CPU time spent.</summary>
    public TimeSpan Render(ReadOnlySpan<GlowSegment> segments)
    {
        if (_canvas is null || _backBuffer is null || _blurOutput is null || _swapChain is null)
        {
            throw new InvalidOperationException("Configure the renderer before rendering.");
        }

        var started = Stopwatch.GetTimestamp();

        _context.Target = _canvas;
        _context.BeginDraw();
        _context.Clear(Transparent);
        foreach (var segment in segments)
        {
            _brush.Color = new Color4(segment.Red, segment.Green, segment.Blue, segment.Alpha);
            _context.FillRectangle(new Vortice.RawRectF(segment.Left, segment.Top, segment.Right, segment.Bottom), _brush);
        }

        CheckEndDraw(_context.EndDraw());

        _context.Target = _backBuffer;
        _context.BeginDraw();
        _context.Clear(Transparent);
        var offset = new Vector2(-_layout.Margin, -_layout.Margin);
        _context.DrawImage(_blurOutput, in offset, D2DInterpolationMode.Linear, D2DCompositeMode.SourceOver);
        CheckEndDraw(_context.EndDraw());

        var present = _swapChain.Present(1, PresentFlags.None);
        if (present.Failure)
        {
            if (IsDeviceLoss(present.Code))
            {
                throw new OverlayDeviceLostException($"Present failed: {present}");
            }

            present.CheckError();
        }

        if (_commitPending)
        {
            var commit = _compositionDevice.Commit();
            if (commit.Failure && IsDeviceLoss(commit.Code))
            {
                throw new OverlayDeviceLostException($"DirectComposition commit failed: {commit}");
            }

            commit.CheckError();
            _commitPending = false;
        }

        return Stopwatch.GetElapsedTime(started);
    }

    public void Dispose() => ReleaseInterfaces(disposeDevice: true);

    private static bool IsDeviceLoss(int code) =>
        code == DxgiResult.DeviceRemoved.Code ||
        code == DxgiResult.DeviceReset.Code ||
        code == DxgiResult.DeviceHung.Code ||
        code == D2DErrorRecreateTarget;

    private static void CheckEndDraw(Result result)
    {
        if (result.Code == D2DErrorRecreateTarget)
        {
            throw new OverlayDeviceLostException("Direct2D requested the render target to be recreated.");
        }

        result.CheckError();
    }

    private static IDXGIAdapter1? FindAdapter(IDXGIFactory1 factory, string monitorDeviceName)
    {
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
            {
                using (output)
                {
                    if (string.Equals(output.Description.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return adapter;
                    }
                }
            }

            adapter.Dispose();
        }

        return null;
    }

    private void CreateSwapChain(int width, int height)
    {
        _context.Target = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain?.Dispose();

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = DxgiAlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };

        _swapChain = _dxgiFactory.CreateSwapChainForComposition(_device, description);
        using (var surface = _swapChain.GetBuffer<IDXGISurface>(0))
        {
            _backBuffer = _context.CreateBitmapFromDxgiSurface(
                surface,
                new BitmapProperties1(PremultipliedBgra, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        _visual.SetContent(_swapChain).CheckError();
    }

    private void CreateCanvas(int width, int height)
    {
        _blurOutput?.Dispose();
        _blurOutput = null;
        _canvas?.Dispose();

        var size = new SizeI(width, height);
        _canvas = _context.CreateBitmap(size, new BitmapProperties1(PremultipliedBgra, 96f, 96f, BitmapOptions.Target));
        _blur.SetInput(0, _canvas, true);
        _blurOutput = _blur.Output;
    }

    // Null-tolerant so it also cleans up after a constructor that failed part-way.
    private void ReleaseInterfaces(bool disposeDevice)
    {
        if (_context is not null)
        {
            _context.Target = null;
        }

        ReleaseSizedResources();
        _visual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _blur?.Dispose();
        _brush?.Dispose();
        _context?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _dxgiFactory?.Dispose();
        _dxgiDevice?.Dispose();
        if (disposeDevice)
        {
            _device.Dispose();
        }
    }

    private void ReleaseSizedResources()
    {
        _blurOutput?.Dispose();
        _blurOutput = null;
        _canvas?.Dispose();
        _canvas = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }
}
