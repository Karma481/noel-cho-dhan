using System.Runtime.InteropServices;

namespace AmbientLight.Capture.Interop;

/// <summary>
/// Reads the "SDR content brightness" (SDR reference white level) Windows uses for a monitor in HDR
/// mode, through the CCD (Connecting and Configuring Displays) API.
/// </summary>
/// <remarks>
/// In an HDR desktop, DWM composes SDR content at this luminance, so the scRGB value of SDR white is
/// <c>whiteLevelNits / 80</c>. Dividing captured scRGB by it makes SDR content read as 0..1 whatever the
/// slider position, and leaves only real HDR highlights above 1.
/// </remarks>
internal static unsafe partial class DisplayConfigInterop
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int DeviceInfoGetSourceName = 1;
    private const int DeviceInfoGetSdrWhiteLevel = 11;
    private const int MaxQueryAttempts = 4;

    /// <summary>Nits represented by an SDR white level value of 1000 (the scRGB 1.0 reference).</summary>
    private const float NitsPerThousand = 80f;

    /// <summary>
    /// Returns the SDR white level in nits for the display whose GDI name is
    /// <paramref name="gdiDeviceName"/> (for example <c>\\.\DISPLAY1</c>), or <see langword="null"/>
    /// when it cannot be determined (no active path, API unavailable before Windows 10 1709).
    /// </summary>
    public static float? TryGetSdrWhiteLevelNits(string gdiDeviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(gdiDeviceName);

        if (!TryQueryActivePaths(out var paths))
        {
            return null;
        }

        foreach (var path in paths)
        {
            var sourceName = new SourceDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSourceName,
                    Size = (uint)sizeof(SourceDeviceName),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
            };

            if (DisplayConfigGetDeviceInfo(&sourceName.Header) != ErrorSuccess)
            {
                continue;
            }

            var name = new string(sourceName.ViewGdiDeviceName, 0, SourceDeviceName.NameLength).TrimEnd('\0');
            if (!string.Equals(name, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var whiteLevel = new SdrWhiteLevel
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSdrWhiteLevel,
                    Size = (uint)sizeof(SdrWhiteLevel),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };

            if (DisplayConfigGetDeviceInfo(&whiteLevel.Header) != ErrorSuccess || whiteLevel.WhiteLevel == 0)
            {
                return null;
            }

            return whiteLevel.WhiteLevel / 1000f * NitsPerThousand;
        }

        return null;
    }

    private static bool TryQueryActivePaths(out PathInfo[] paths)
    {
        for (var attempt = 0; attempt < MaxQueryAttempts; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != ErrorSuccess)
            {
                break;
            }

            var pathBuffer = new PathInfo[pathCount];
            var modeBuffer = new ModeInfo[modeCount];
            int status;
            fixed (PathInfo* pathPointer = pathBuffer)
            fixed (ModeInfo* modePointer = modeBuffer)
            {
                status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathPointer, ref modeCount, modePointer, IntPtr.Zero);
            }

            if (status == ErrorSuccess)
            {
                paths = pathBuffer.AsSpan(0, (int)pathCount).ToArray();
                return true;
            }

            // The topology changed between the two calls (monitor hot-plug); size again and retry.
            if (status != ErrorInsufficientBuffer)
            {
                break;
            }
        }

        paths = [];
        return false;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        PathInfo* pathArray,
        ref uint numModeInfoArrayElements,
        ModeInfo* modeInfoArray,
        IntPtr currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // DISPLAYCONFIG_PATH_SOURCE_INFO (20 bytes). The union of modeInfoIdx / clone-group bit fields is one UINT32.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    // DISPLAYCONFIG_PATH_TARGET_INFO (48 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    // DISPLAYCONFIG_PATH_INFO (72 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO (64 bytes, 8-byte aligned because the union contains a UINT64).
    // Only its size matters here; the mode payload is never read.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo
    {
        public int InfoType;
        public uint Id;
        public Luid AdapterId;
        public ulong UnionStart;
    }

    // DISPLAYCONFIG_DEVICE_INFO_HEADER (20 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    // DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceDeviceName
    {
        public const int NameLength = 32;

        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[NameLength];
    }

    // DISPLAYCONFIG_SDR_WHITE_LEVEL (24 bytes).
    [StructLayout(LayoutKind.Sequential)]
    internal struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint WhiteLevel;
    }
}
