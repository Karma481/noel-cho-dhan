using System.Globalization;
using System.Text.Json.Serialization;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Settings;

/// <summary>
/// Root of the persisted configuration.
/// </summary>
/// <remarks>
/// Every settings type is an immutable record. A change in the UI produces a new <see cref="AppSettings"/>
/// instance which is published with a single reference swap (<see cref="SettingsHolder"/>); pipeline
/// threads pick it up at the start of their next iteration, so reconfiguration never takes a lock on
/// the hot path and a thread never observes a half-applied change.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Current schema version, bumped whenever a breaking change to the JSON layout is made.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the persisted document.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Screen capture configuration.</summary>
    public CaptureSettings Capture { get; init; } = new();

    /// <summary>Color processing configuration.</summary>
    public ProcessingSettings Processing { get; init; } = new();

    /// <summary>Virtual on-screen glow configuration.</summary>
    public OverlaySettings Overlay { get; init; } = new();

    /// <summary>Physical LED strip serial output configuration.</summary>
    public SerialSettings Serial { get; init; } = new();

    /// <summary>LED strip geometry around the screen.</summary>
    public LedLayoutSettings LedLayout { get; init; } = new();

    /// <summary>Validates every section and cross-section constraint.</summary>
    public IReadOnlyList<SettingsIssue> Validate()
    {
        var issues = new List<SettingsIssue>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add(SettingsIssue.Error(
                "schemaVersion",
                string.Create(CultureInfo.InvariantCulture, $"Unsupported schema version {SchemaVersion}; expected {CurrentSchemaVersion}.")));
        }

        Capture.Validate(issues);
        Processing.Validate(issues);
        Overlay.Validate(issues);
        Serial.Validate(issues);
        LedLayout.Validate(issues);

        if (Serial.Enabled && LedLayout.TotalLedCount > 0)
        {
            var transmit = Serial.EstimateTransmitTime(LedLayout.TotalLedCount);
            var refreshPeriod = TimeSpan.FromSeconds(1.0 / Serial.MaxRefreshHz);
            if (transmit > refreshPeriod)
            {
                issues.Add(SettingsIssue.Warning(
                    "serial.baudRate",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"One frame of {LedLayout.TotalLedCount} LEDs needs {transmit.TotalMilliseconds:F1} ms at {Serial.BaudRate} baud, longer than the {refreshPeriod.TotalMilliseconds:F1} ms refresh period. Raise the baud rate or lower maxRefreshHz.")));
            }
        }

        return issues;
    }
}

/// <summary>DXGI Desktop Duplication capture settings.</summary>
public sealed record CaptureSettings
{
    /// <summary>
    /// GDI device name of the monitor to capture (for example <c>\\.\DISPLAY1</c>);
    /// <see langword="null"/> selects the primary monitor.
    /// </summary>
    public string? OutputDeviceName { get; init; }

    /// <summary>Upper bound on processed frames per second. Capture is event-driven below this cap.</summary>
    public int MaxFps { get; init; } = 60;

    /// <summary>Samples taken along each axis of a zone by the GPU reduction (N x N samples per zone).</summary>
    public int SamplesPerZoneAxis { get; init; } = 16;

    /// <summary>Timeout passed to <c>AcquireNextFrame</c>; also bounds how quickly shutdown is noticed.</summary>
    public int AcquireTimeoutMs { get; init; } = 100;

    /// <summary>Tone-map HDR (scRGB FP16) desktops down to SDR before sampling.</summary>
    public bool HdrToneMapping { get; init; } = true;

    internal void Validate(List<SettingsIssue> issues)
    {
        RangeCheck.Int(issues, "capture.maxFps", MaxFps, 1, 240);
        RangeCheck.Int(issues, "capture.samplesPerZoneAxis", SamplesPerZoneAxis, 1, 64);
        RangeCheck.Int(issues, "capture.acquireTimeoutMs", AcquireTimeoutMs, 1, 1000);
        if (OutputDeviceName is { Length: 0 })
        {
            issues.Add(SettingsIssue.Error("capture.outputDeviceName", "Must be null (primary monitor) or a non-empty device name."));
        }
    }
}

/// <summary>Per-channel multipliers used for LED white balance.</summary>
public readonly record struct RgbGain(float R, float G, float B)
{
    /// <summary>No correction.</summary>
    public static RgbGain Identity => new(1f, 1f, 1f);
}

/// <summary>CPU-side color pipeline settings.</summary>
public sealed record ProcessingSettings
{
    /// <summary>Time constant of the exponential temporal smoothing, in milliseconds. 0 disables smoothing.</summary>
    public int SmoothingTimeMs { get; init; } = 80;

    /// <summary>Global brightness multiplier, 0..1.</summary>
    public float Brightness { get; init; } = 1f;

    /// <summary>Saturation multiplier, 0..2 (1 = unchanged).</summary>
    public float Saturation { get; init; } = 1.1f;

    /// <summary>Gamma applied to compensate the LEDs' linear PWM response, 1..3.</summary>
    public float LedGamma { get; init; } = 2.2f;

    /// <summary>White balance gains for the physical LEDs, each 0..1.</summary>
    public RgbGain WhiteBalance { get; init; } = RgbGain.Identity;

    /// <summary>Colors whose luma is below this 0..255 value are output as black to avoid flicker on dark scenes.</summary>
    public int BlackThreshold { get; init; } = 6;

    internal void Validate(List<SettingsIssue> issues)
    {
        RangeCheck.Int(issues, "processing.smoothingTimeMs", SmoothingTimeMs, 0, 2000);
        RangeCheck.Float(issues, "processing.brightness", Brightness, 0f, 1f);
        RangeCheck.Float(issues, "processing.saturation", Saturation, 0f, 2f);
        RangeCheck.Float(issues, "processing.ledGamma", LedGamma, 1f, 3f);
        RangeCheck.Float(issues, "processing.whiteBalance.r", WhiteBalance.R, 0f, 1f);
        RangeCheck.Float(issues, "processing.whiteBalance.g", WhiteBalance.G, 0f, 1f);
        RangeCheck.Float(issues, "processing.whiteBalance.b", WhiteBalance.B, 0f, 1f);
        RangeCheck.Int(issues, "processing.blackThreshold", BlackThreshold, 0, 64);
    }
}

/// <summary>Virtual glow overlay settings.</summary>
public sealed record OverlaySettings
{
    /// <summary>Whether the click-through glow overlay is shown.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Glow band thickness as a fraction of the shorter screen dimension.</summary>
    public float ThicknessFraction { get; init; } = 0.06f;

    /// <summary>Peak opacity of the glow at the screen edge, 0..1.</summary>
    public float Opacity { get; init; } = 0.85f;

    /// <summary>
    /// The glow is rendered at 1/N of its on-screen size and upscaled bilinearly by DirectComposition.
    /// Because the glow is low-frequency this is visually free and divides fill cost by N squared.
    /// </summary>
    public int ResolutionDivisor { get; init; } = 8;

    internal void Validate(List<SettingsIssue> issues)
    {
        RangeCheck.Float(issues, "overlay.thicknessFraction", ThicknessFraction, 0.005f, 0.25f);
        RangeCheck.Float(issues, "overlay.opacity", Opacity, 0f, 1f);
        RangeCheck.Int(issues, "overlay.resolutionDivisor", ResolutionDivisor, 1, 32);
    }
}

/// <summary>Wire protocol spoken to the LED controller.</summary>
public enum LedProtocol
{
    /// <summary>"Ada" + count-hi + count-lo + checksum header followed by RGB triplets.</summary>
    Adalight = 0,
}

/// <summary>Serial LED controller settings.</summary>
public sealed record SerialSettings
{
    /// <summary>Size of the Adalight frame header in bytes.</summary>
    public const int AdalightHeaderBytes = 6;

    /// <summary>Bits on the wire per byte with 8N1 framing (start + 8 data + stop).</summary>
    public const int BitsPerByte8N1 = 10;

    /// <summary>Whether frames are sent to the LED controller.</summary>
    public bool Enabled { get; init; }

    /// <summary>Serial port name, for example <c>COM3</c>.</summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>
    /// Baud rate. Native USB-CDC boards (ESP32-S2/S3/C3) ignore it and run at USB speed; boards behind a
    /// USB-UART bridge are limited by the bridge (CH340: 2,000,000; CP2102N: 3,000,000).
    /// </summary>
    public int BaudRate { get; init; } = 1_000_000;

    /// <summary>Wire protocol.</summary>
    public LedProtocol Protocol { get; init; } = LedProtocol.Adalight;

    /// <summary>Maximum frames per second sent to the controller.</summary>
    public int MaxRefreshHz { get; init; } = 60;

    /// <summary>The last frame is re-sent after this much idle time so the firmware's blank-on-timeout never fires.</summary>
    public int KeepAliveMs { get; init; } = 500;

    /// <summary>Power supply budget in milliamps for automatic brightness limiting; 0 disables the limiter.</summary>
    public int MaxCurrentMilliamps { get; init; }

    /// <summary>Current drawn by one color channel at full duty (WS2812B: about 20 mA).</summary>
    public int MilliampsPerChannel { get; init; } = 20;

    /// <summary>Estimated time on the wire for one frame of <paramref name="ledCount"/> LEDs.</summary>
    public TimeSpan EstimateTransmitTime(int ledCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledCount);
        var bytes = AdalightHeaderBytes + (ledCount * 3);
        return TimeSpan.FromSeconds((double)bytes * BitsPerByte8N1 / BaudRate);
    }

    internal void Validate(List<SettingsIssue> issues)
    {
        if (Enabled && string.IsNullOrWhiteSpace(PortName))
        {
            issues.Add(SettingsIssue.Error("serial.portName", "A port name is required when serial output is enabled."));
        }

        RangeCheck.Int(issues, "serial.baudRate", BaudRate, 9_600, 12_000_000);
        RangeCheck.Int(issues, "serial.maxRefreshHz", MaxRefreshHz, 1, 240);
        RangeCheck.Int(issues, "serial.keepAliveMs", KeepAliveMs, 50, 5_000);
        RangeCheck.Int(issues, "serial.maxCurrentMilliamps", MaxCurrentMilliamps, 0, 100_000);
        RangeCheck.Int(issues, "serial.milliampsPerChannel", MilliampsPerChannel, 1, 100);
    }
}

/// <summary>Physical LED strip geometry around the screen.</summary>
public sealed record LedLayoutSettings
{
    /// <summary>Hard cap on LEDs; keeps per-frame buffers small and the Adalight count field valid.</summary>
    public const int MaxLedCount = 1024;

    /// <summary>LEDs along the top edge.</summary>
    public int TopCount { get; init; } = 32;

    /// <summary>LEDs along the right edge.</summary>
    public int RightCount { get; init; } = 18;

    /// <summary>LEDs along the bottom edge.</summary>
    public int BottomCount { get; init; } = 32;

    /// <summary>LEDs along the left edge.</summary>
    public int LeftCount { get; init; } = 18;

    /// <summary>Corner where the strip's first LED is mounted.</summary>
    public StripStartCorner StartCorner { get; init; } = StripStartCorner.BottomLeft;

    /// <summary>Direction the strip runs, seen from the front of the screen.</summary>
    public StripDirection Direction { get; init; } = StripDirection.Clockwise;

    /// <summary>How far each zone reaches into the picture, as a fraction of the screen dimension.</summary>
    public float SampleDepth { get; init; } = 0.10f;

    /// <summary>Total LEDs on the strip.</summary>
    [JsonIgnore]
    public int TotalLedCount => TopCount + RightCount + BottomCount + LeftCount;

    internal void Validate(List<SettingsIssue> issues)
    {
        RangeCheck.Int(issues, "ledLayout.topCount", TopCount, 0, MaxLedCount);
        RangeCheck.Int(issues, "ledLayout.rightCount", RightCount, 0, MaxLedCount);
        RangeCheck.Int(issues, "ledLayout.bottomCount", BottomCount, 0, MaxLedCount);
        RangeCheck.Int(issues, "ledLayout.leftCount", LeftCount, 0, MaxLedCount);
        RangeCheck.Float(issues, "ledLayout.sampleDepth", SampleDepth, 0.01f, 0.5f);
        if (TotalLedCount is < 1 or > MaxLedCount)
        {
            issues.Add(SettingsIssue.Error(
                "ledLayout",
                string.Create(CultureInfo.InvariantCulture, $"Total LED count {TotalLedCount} must be between 1 and {MaxLedCount}.")));
        }

        if (!Enum.IsDefined(StartCorner))
        {
            issues.Add(SettingsIssue.Error("ledLayout.startCorner", "Unknown start corner."));
        }

        if (!Enum.IsDefined(Direction))
        {
            issues.Add(SettingsIssue.Error("ledLayout.direction", "Unknown direction."));
        }
    }
}

internal static class RangeCheck
{
    public static void Int(List<SettingsIssue> issues, string path, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            issues.Add(SettingsIssue.Error(
                path,
                string.Create(CultureInfo.InvariantCulture, $"Value {value} is outside the allowed range {min}..{max}.")));
        }
    }

    public static void Float(List<SettingsIssue> issues, string path, float value, float min, float max)
    {
        if (!float.IsFinite(value) || value < min || value > max)
        {
            issues.Add(SettingsIssue.Error(
                path,
                string.Create(CultureInfo.InvariantCulture, $"Value {value} is outside the allowed range {min}..{max}.")));
        }
    }
}
