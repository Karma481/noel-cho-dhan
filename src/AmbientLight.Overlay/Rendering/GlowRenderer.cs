using System.Diagnostics;
using System.Numerics;
using AmbientLight.Core.Settings;
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
/// Draws the two-layer glow into a DirectComposition swapchain attached to the overlay window.
/// </summary>
/// <remarks>
/// <para>Per redraw, all on this renderer's own D3D11 device (never shared with the capture thread):</para>
/// <code>
/// inner canvas ◄─ FillRectangle per zone (thin bands) ─► Gaussian blur (narrow) ──┐
///                                                                                ├─► arithmetic composite: screen
/// wash canvas  ◄─ FillRectangle per zone (wide bands) ─► Gaussian blur (wide)  ──┘   a + b − a·b
///                                                                                       │
///                                     color matrix: output alpha for the blend mode ◄───┘
///                                                       │ DrawImage (source copy) at −margin,
///                                                       │ then clear the picture of a letterboxed video
///                                                       ▼
/// swapchain back buffer (screen / divisor) ─ Present(1) ─► DirectComposition visual (scale × divisor, bilinear)
///                                                             └─► DWM: result = glow + screen × (1 − alpha)
/// </code>
/// <para>
/// <b>Layers.</b> The inner glow is a thin band with a small blur, so it is intense right at the bezel; the
/// ambient wash is a wide band with a very large blur that tints the screen far inwards. They are combined with
/// a screen blend on premultiplied values, which, unlike a sum, can never exceed full intensity, so the colors
/// saturate smoothly instead of being clipped channel by channel.
/// </para>
/// <para>
/// <b>Blend modes.</b> DWM composes the premultiplied swapchain as <c>glow + screen × (1 − alpha)</c>. The
/// color matrix (in straight mode, so it works on the premultiplied values as they are) only rewrites alpha:
/// </para>
/// <list type="bullet">
/// <item><see cref="OverlayBlendMode.Normal"/>: alpha unchanged, ordinary "over" painting.</item>
/// <item><see cref="OverlayBlendMode.Screen"/>: alpha = luminance of the glow. Dark glow is transparent, and
/// bright content under a colored glow is barely dimmed, which approximates a per-channel screen blend
/// (<c>glow + screen × (1 − glow)</c>) with the single alpha channel DWM supports.</item>
/// <item><see cref="OverlayBlendMode.Additive"/>: alpha = 0, so DWM adds the glow to the screen.</item>
/// </list>
/// <para>
/// Rendering at 1/8 of the screen resolution makes both blurs 64 times cheaper; the glow is a low-frequency
/// signal, so the bilinear upscale done by DWM is visually indistinguishable from a full-resolution render. No
/// GPU object is created per frame: canvases, brush and effects are created when the layout changes and reused.
/// </para>
/// </remarks>
internal sealed class GlowRenderer : IDisposable
{
    private const int D2DErrorRecreateTarget = unchecked((int)0x8899000C);

    /// <summary>Screen blend of the two layers on premultiplied values: C1·a·b + C2·a + C3·b + C4 = a + b − a·b.</summary>
    private static readonly Vector4 ScreenCoefficients = new(-1f, 1f, 1f, 0f);

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
    private readonly GaussianBlur _innerBlur;
    private readonly GaussianBlur _washBlur;
    private readonly ArithmeticComposite _combine;
    private readonly ColorMatrix _blend;
    private readonly ID2D1Image _output;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _visual;

    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _backBuffer;
    private ID2D1Bitmap1? _innerCanvas;
    private ID2D1Bitmap1? _washCanvas;
    private GlowLayout _layout;
    private OverlayBlendMode? _blendMode;
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
            _innerBlur = CreateBlur(_context);
            _washBlur = CreateBlur(_context);

            _combine = new ArithmeticComposite(_context) { Coefficients = ScreenCoefficients, ClampOutput = true };
            _combine.SetInputEffect(0, _innerBlur, true);
            _combine.SetInputEffect(1, _washBlur, true);

            _blend = new ColorMatrix(_context) { AlphaMode = ColorMatrixAlphaMode.Straight };
            _blend.SetInputEffect(0, _combine, true);
            _output = _blend.Output;

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
    /// Applies a layout and blend mode: (re)creates the swapchain when the render size changes, the canvases when
    /// the margin changes, updates both blur strengths, the blend matrix and the composition scale, and commits
    /// the visual tree.
    /// </summary>
    public void Configure(in GlowLayout layout, OverlayBlendMode blendMode)
    {
        try
        {
            if (_swapChain is null || layout.RenderWidth != _layout.RenderWidth || layout.RenderHeight != _layout.RenderHeight)
            {
                CreateSwapChain(layout.RenderWidth, layout.RenderHeight);
            }

            if (_innerCanvas is null || layout.CanvasWidth != _layout.CanvasWidth || layout.CanvasHeight != _layout.CanvasHeight)
            {
                CreateCanvases(layout.CanvasWidth, layout.CanvasHeight);
            }

            _innerBlur.StandardDeviation = Math.Clamp(layout.InnerSigma, 0f, GlowGeometry.MaxBlurSigma);
            _washBlur.StandardDeviation = Math.Clamp(layout.WashSigma, 0f, GlowGeometry.MaxBlurSigma);
            if (_blendMode != blendMode)
            {
                _blend.Matrix = BlendMatrix(blendMode);
                _blendMode = blendMode;
            }

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

    /// <summary>
    /// The color matrix that turns the combined (premultiplied) glow into what DWM should compose. Rows are the
    /// input R, G, B, A and a constant; columns the output R, G, B, A. Color passes through unchanged.
    /// </summary>
    internal static Matrix5x4 BlendMatrix(OverlayBlendMode blendMode)
    {
        var (fromRed, fromGreen, fromBlue, fromAlpha) = blendMode switch
        {
            OverlayBlendMode.Normal => (0f, 0f, 0f, 1f),
            OverlayBlendMode.Screen => (0.2126f, 0.7152f, 0.0722f, 0f),
            OverlayBlendMode.Additive => (0f, 0f, 0f, 0f),
            _ => throw new ArgumentOutOfRangeException(nameof(blendMode), blendMode, "Unknown blend mode."),
        };

        return new Matrix5x4(
            1f, 0f, 0f, fromRed,
            0f, 1f, 0f, fromGreen,
            0f, 0f, 1f, fromBlue,
            0f, 0f, 0f, fromAlpha,
            0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// Draws both layers' bands (canvas coordinates), runs the blur/blend graph, clears <paramref name="mask"/>
    /// (the picture of a letterboxed video) and presents. Returns the CPU time spent.
    /// </summary>
    public TimeSpan Render(ReadOnlySpan<GlowSegment> innerSegments, ReadOnlySpan<GlowSegment> washSegments, PictureMask? mask)
    {
        if (_innerCanvas is null || _washCanvas is null || _backBuffer is null || _swapChain is null)
        {
            throw new InvalidOperationException("Configure the renderer before rendering.");
        }

        var started = Stopwatch.GetTimestamp();

        DrawBands(_innerCanvas, innerSegments);
        DrawBands(_washCanvas, washSegments);

        _context.Target = _backBuffer;
        _context.BeginDraw();
        _context.Clear(Transparent);
        var offset = new Vector2(-_layout.Margin, -_layout.Margin);

        // Source copy: the blend matrix may produce color without alpha (additive), which must reach the
        // swapchain exactly as computed rather than be composited again.
        _context.DrawImage(_output, in offset, D2DInterpolationMode.Linear, D2DCompositeMode.SourceCopy);
        if (mask is { } picture)
        {
            // Clear honours the axis-aligned clip, so this empties exactly the picture area.
            _context.PushAxisAlignedClip(new Vortice.RawRectF(picture.Left, picture.Top, picture.Right, picture.Bottom), AntialiasMode.Aliased);
            _context.Clear(Transparent);
            _context.PopAxisAlignedClip();
        }

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

    private static GaussianBlur CreateBlur(ID2D1DeviceContext context) => new(context)
    {
        BorderMode = D2DBorderMode.Hard,
        Optimization = GaussianBlurOptimization.Balanced,
    };

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

    // Integrated GPU first (minimum-power order, DXGI 1.6) so a hybrid laptop renders the glow on the GPU that
    // drives the panel, which spares DWM a cross-adapter copy and keeps the discrete GPU asleep.
    private static IDXGIAdapter1? FindAdapter(IDXGIFactory1 factory, string monitorDeviceName)
    {
        using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var enumerated = factory6 is not null
                ? factory6.EnumAdapterByGpuPreference(adapterIndex, GpuPreference.MinimumPower, out IDXGIAdapter1? adapter)
                : factory.EnumAdapters1(adapterIndex, out adapter);
            if (enumerated.Failure || adapter is null)
            {
                return null;
            }

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

    private void DrawBands(ID2D1Bitmap1 canvas, ReadOnlySpan<GlowSegment> segments)
    {
        _context.Target = canvas;
        _context.BeginDraw();
        _context.Clear(Transparent);
        foreach (var segment in segments)
        {
            _brush.Color = new Color4(segment.Red, segment.Green, segment.Blue, segment.Alpha);
            _context.FillRectangle(new Vortice.RawRectF(segment.Left, segment.Top, segment.Right, segment.Bottom), _brush);
        }

        CheckEndDraw(_context.EndDraw());
    }

    private void CreateCanvases(int width, int height)
    {
        _context.Target = null;
        _innerCanvas?.Dispose();
        _washCanvas?.Dispose();

        var size = new SizeI(width, height);
        var properties = new BitmapProperties1(PremultipliedBgra, 96f, 96f, BitmapOptions.Target);
        _innerCanvas = _context.CreateBitmap(size, properties);
        _washCanvas = _context.CreateBitmap(size, properties);
        _innerBlur.SetInput(0, _innerCanvas, true);
        _washBlur.SetInput(0, _washCanvas, true);
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
        _output?.Dispose();
        _blend?.Dispose();
        _combine?.Dispose();
        _washBlur?.Dispose();
        _innerBlur?.Dispose();
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
        _innerCanvas?.Dispose();
        _innerCanvas = null;
        _washCanvas?.Dispose();
        _washCanvas = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }
}
