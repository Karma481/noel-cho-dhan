using AmbientLight.Host.Platform;
using Microsoft.Extensions.Logging;

namespace AmbientLight.App;

/// <summary>Source-generated log messages of the app shell (event ids 6000-6099).</summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Ambient Light {Version} starting on {OperatingSystem}")]
    public static partial void Starting(ILogger logger, string version, string operatingSystem);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Ambient Light exiting")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Exit requested by another launch (--exit)")]
    public static partial void ExitRequested(ILogger logger);

    [LoggerMessage(EventId = 6010, Level = LogLevel.Information, Message = "Effect {State}")]
    private static partial void PauseToggledCore(ILogger logger, string state);

    public static void PauseToggled(ILogger logger, bool paused) => PauseToggledCore(logger, paused ? "paused" : "resumed");

    [LoggerMessage(EventId = 6011, Level = LogLevel.Information, Message = "Overlay turned {State} by hotkey")]
    private static partial void OverlayToggledCore(ILogger logger, string state);

    public static void OverlayToggled(ILogger logger, bool enabled) => OverlayToggledCore(logger, enabled ? "on" : "off");

    [LoggerMessage(EventId = 6012, Level = LogLevel.Warning, Message = "Overlay hotkey ignored: {Issues}")]
    public static partial void OverlayToggleRejected(ILogger logger, string issues);

    [LoggerMessage(EventId = 6013, Level = LogLevel.Warning,
        Message = "Hotkey {Gesture} is already used by another application and is not available")]
    public static partial void HotkeyUnavailable(ILogger logger, string gesture);

    [LoggerMessage(EventId = 6015, Level = LogLevel.Warning,
        Message = "The tray icon could not be added ({Reason}); retrying every few seconds")]
    public static partial void TrayUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 6014, Level = LogLevel.Information, Message = "Exclusive fullscreen {Active}")]
    public static partial void FullscreenChanged(ILogger logger, bool active);

    [LoggerMessage(EventId = 6020, Level = LogLevel.Information, Message = "Autostart entry re-pointed to {Path} after the executable moved")]
    public static partial void AutostartRepaired(ILogger logger, string path);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Information, Message = "Windows GPU preference: {Outcome}")]
    public static partial void GpuPreference(ILogger logger, GpuPreferenceOutcome outcome);

    [LoggerMessage(EventId = 6022, Level = LogLevel.Warning, Message = "Registry update failed")]
    public static partial void RegistryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6030, Level = LogLevel.Critical, Message = "Unhandled exception; the process is terminating")]
    public static partial void Crash(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6031, Level = LogLevel.Error, Message = "Unhandled UI exception (ignored, the pipeline keeps running)")]
    public static partial void UiError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6040, Level = LogLevel.Warning, Message = "Settings change rejected: {Issues}")]
    public static partial void SettingsRejected(ILogger logger, string issues);

    [LoggerMessage(EventId = 6041, Level = LogLevel.Warning, Message = "Could not change the autostart entry")]
    public static partial void AutostartFailed(ILogger logger, Exception exception);
}
