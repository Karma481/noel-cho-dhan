using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AmbientLight.Capture.Recovery;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Zones;
using Vortice.Direct3D11;

namespace AmbientLight.Capture.Gpu;

/// <summary>
/// Runs the ZoneReduce.hlsl kernels over a desktop surface and reads the per-zone colors (and, when
/// letterbox detection is on, the per-line luminance profile) back to the CPU.
/// </summary>
/// <remarks>
/// <para>
/// All buffers are sized once for <see cref="Capacity"/> zones, so changing the LED layout never
/// reallocates GPU memory. Per frame the CPU uploads at most 32 bytes of constants, and reads back
/// 16 bytes per zone.
/// </para>
/// <para>
/// Completion is awaited with an <see cref="ID3D11Fence"/> and a kernel event, so the capture thread
/// sleeps (0% CPU) while the GPU works instead of spinning inside a blocking <c>Map</c>. Devices
/// without fence support fall back to the blocking <c>Map</c>.
/// </para>
/// </remarks>
internal sealed class GpuZoneReducer : IDisposable
{
    /// <summary>Generous bound for a few microseconds of GPU work; exceeding it means the GPU is hung.</summary>
    private static readonly TimeSpan GpuCompletionTimeout = TimeSpan.FromMilliseconds(500);

    private const int OutputStride = 16;
    private const int ProfileLength = ZoneSampleFrame.ProfileResolution * 2;

    private readonly CaptureDevice _device;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _zoneBuffer;
    private readonly ID3D11ShaderResourceView _zoneView;
    private readonly ID3D11Buffer _outputBuffer;
    private readonly ID3D11UnorderedAccessView _outputView;
    private readonly ID3D11Buffer _stagingBuffer;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11ComputeShader _profileShader;
    private readonly ID3D11Buffer _profileBuffer;
    private readonly ID3D11UnorderedAccessView _profileView;
    private readonly ID3D11Buffer _profileStagingBuffer;
    private readonly ID3D11Buffer _profileConstantBuffer;
    private readonly ID3D11Fence? _fence;
    private readonly AutoResetEvent _fenceEvent = new(initialState: false);
    private readonly ZoneConfig[] _zoneUpload;

    private ZoneReduceConstants _uploadedConstants;
    private bool _constantsUploaded;
    private EdgeProfileConstants _uploadedProfileConstants;
    private bool _profileConstantsUploaded;
    private ulong _fenceValue;

    public GpuZoneReducer(CaptureDevice device, int capacity)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _device = device;
        Capacity = capacity;
        _zoneUpload = new ZoneConfig[capacity];
        var d3d = device.Device;

        _shader = d3d.CreateComputeShader(ZoneReduceShader.ZoneReduce);

        _zoneBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(ZoneConfig.GpuStride * capacity),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = ZoneConfig.GpuStride,
        });
        _zoneView = d3d.CreateShaderResourceView(_zoneBuffer);

        _outputBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(OutputStride * capacity),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = OutputStride,
        });
        _outputView = d3d.CreateUnorderedAccessView(_outputBuffer);

        _stagingBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(OutputStride * capacity),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = OutputStride,
        });

        _constantBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = ZoneReduceConstants.SizeInBytes,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        });

        _sampler = d3d.CreateSamplerState(SamplerDescription.LinearClamp);

        _profileShader = d3d.CreateComputeShader(ZoneReduceShader.EdgeProfile);
        _profileBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = sizeof(float) * ProfileLength,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _profileView = d3d.CreateUnorderedAccessView(_profileBuffer);
        _profileStagingBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = sizeof(float) * ProfileLength,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _profileConstantBuffer = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = EdgeProfileConstants.SizeInBytes,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        });

        _fence = TryCreateFence(device);
    }

    /// <summary>Maximum number of zones.</summary>
    public int Capacity { get; }

    /// <summary>True when GPU completion is awaited with a fence rather than a blocking Map.</summary>
    public bool UsesFence => _fence is not null && _device.Context4 is not null;

    /// <summary>Uploads zones already transformed to texture space. Call only when the layout or rotation changes.</summary>
    public void UploadZones(ReadOnlySpan<ZoneConfig> textureSpaceZones)
    {
        if (textureSpaceZones.Length > Capacity)
        {
            throw new ArgumentException($"{textureSpaceZones.Length} zones exceed the reducer capacity of {Capacity}.", nameof(textureSpaceZones));
        }

        textureSpaceZones.CopyTo(_zoneUpload);
        _zoneUpload.AsSpan(textureSpaceZones.Length).Clear();
        _device.Context.UpdateSubresource(_zoneUpload, _zoneBuffer);
    }

    /// <summary>
    /// Reduces <paramref name="desktop"/> to <c>constants.ZoneCount</c> colors written to
    /// <paramref name="destination"/> and, when <paramref name="profileDestination"/> is not empty, also
    /// computes the row/column luminance profile into it. Both kernels share one submission and one fence
    /// wait. Returns the time from submission to data on the CPU.
    /// </summary>
    public TimeSpan Reduce(
        ID3D11ShaderResourceView desktop,
        in ZoneReduceConstants constants,
        Span<Vector3> destination,
        in EdgeProfileConstants profileConstants,
        Span<float> profileDestination)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var zoneCount = (int)constants.ZoneCount;
        if (zoneCount <= 0 || zoneCount > Capacity || destination.Length < zoneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(constants), zoneCount, "Zone count is outside the reducer capacity or the destination size.");
        }

        var withProfile = !profileDestination.IsEmpty;
        if (withProfile && profileDestination.Length != ProfileLength)
        {
            throw new ArgumentException($"The profile destination must hold exactly {ProfileLength} values.", nameof(profileDestination));
        }

        var context = _device.Context;
        var started = Stopwatch.GetTimestamp();

        if (!_constantsUploaded || _uploadedConstants != constants)
        {
            var upload = constants;
            context.UpdateSubresource(in upload, _constantBuffer);
            _uploadedConstants = constants;
            _constantsUploaded = true;
        }

        context.CSSetShader(_shader);
        context.CSSetConstantBuffer(0, _constantBuffer);
        context.CSSetShaderResource(0, _zoneView);
        context.CSSetShaderResource(1, desktop);
        context.CSSetSampler(0, _sampler);
        context.CSSetUnorderedAccessView(0, _outputView);

        context.Dispatch((uint)zoneCount, 1, 1);
        context.CSUnsetUnorderedAccessView(0);

        if (withProfile)
        {
            if (!_profileConstantsUploaded || _uploadedProfileConstants != profileConstants)
            {
                var upload = profileConstants;
                context.UpdateSubresource(in upload, _profileConstantBuffer);
                _uploadedProfileConstants = profileConstants;
                _profileConstantsUploaded = true;
            }

            // b0 stays bound: the profile kernel decodes texels with the same encoding constants.
            context.CSSetShader(_profileShader);
            context.CSSetConstantBuffer(1, _profileConstantBuffer);
            context.CSSetUnorderedAccessView(1, _profileView);
            context.Dispatch(ProfileLength, 1, 1);
            context.CSUnsetUnorderedAccessView(1);
        }

        // Unbind so no pipeline state keeps referencing the duplication surface after ReleaseFrame.
        context.CSUnsetShaderResource(1);

        context.CopyResource(_stagingBuffer, _outputBuffer);
        if (withProfile)
        {
            context.CopyResource(_profileStagingBuffer, _profileBuffer);
        }

        WaitForGpu();
        ReadBack(zoneCount, destination);
        if (withProfile)
        {
            ReadBackProfile(profileDestination);
        }

        return Stopwatch.GetElapsedTime(started);
    }

    public void Dispose()
    {
        _profileConstantBuffer.Dispose();
        _profileStagingBuffer.Dispose();
        _profileView.Dispose();
        _profileBuffer.Dispose();
        _profileShader.Dispose();
        _fence?.Dispose();
        _fenceEvent.Dispose();
        _sampler.Dispose();
        _constantBuffer.Dispose();
        _stagingBuffer.Dispose();
        _outputView.Dispose();
        _outputBuffer.Dispose();
        _zoneView.Dispose();
        _zoneBuffer.Dispose();
        _shader.Dispose();
    }

    private static ID3D11Fence? TryCreateFence(CaptureDevice device)
    {
        if (device.Device5 is null || device.Context4 is null)
        {
            return null;
        }

        try
        {
            return device.Device5.CreateFence(0, FenceFlags.None);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Driver without monitored-fence support (pre-WDDM 2.2): use blocking Map instead.
            return null;
        }
    }

    private void WaitForGpu()
    {
        if (_fence is null || _device.Context4 is null)
        {
            // Map(Read) below blocks until the copy has completed.
            return;
        }

        _fenceValue++;
        _device.Context4.Signal(_fence, _fenceValue);
        _device.Context.Flush();

        if (_fence.CompletedValue >= _fenceValue)
        {
            return;
        }

        _fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
        if (!_fenceEvent.WaitOne(GpuCompletionTimeout))
        {
            _device.ThrowIfDeviceRemoved();
            throw new CaptureException(
                $"The GPU did not finish the zone reduction within {GpuCompletionTimeout.TotalMilliseconds} ms.",
                CaptureRecovery.RecreateDevice);
        }
    }

    private void ReadBack(int zoneCount, Span<Vector3> destination)
    {
        var context = _device.Context;
        var mapped = context.Map(_stagingBuffer, MapMode.Read, MapFlags.None);
        try
        {
            var colors = MemoryMarshal.Cast<byte, Vector4>(mapped.AsSpan(zoneCount * OutputStride));
            for (var i = 0; i < zoneCount; i++)
            {
                var color = colors[i];
                destination[i] = new Vector3(color.X, color.Y, color.Z);
            }
        }
        finally
        {
            context.Unmap(_stagingBuffer);
        }
    }

    private void ReadBackProfile(Span<float> destination)
    {
        var context = _device.Context;
        var mapped = context.Map(_profileStagingBuffer, MapMode.Read, MapFlags.None);
        try
        {
            MemoryMarshal.Cast<byte, float>(mapped.AsSpan(ProfileLength * sizeof(float))).CopyTo(destination);
        }
        finally
        {
            context.Unmap(_profileStagingBuffer);
        }
    }
}
