using AmbientLight.Host.Pipeline;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Host;

/// <summary>Source-generated log messages of the host (event ids 5000-5099).</summary>
internal static partial class HostLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information,
        Message = "Pipeline {Mode}: capture={Capture} overlay={Overlay} serial={Serial} overlaySuspended={OverlaySuspended}")]
    public static partial void PipelineStateChanged(ILogger logger, PipelineMode mode, bool capture, bool overlay, bool serial, bool overlaySuspended);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug, Message = "{Stage} started")]
    public static partial void StageStarted(ILogger logger, string stage);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug, Message = "{Stage} stopped")]
    public static partial void StageStopped(ILogger logger, string stage);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "{Stage} could not start; retrying at the next change")]
    public static partial void StageStartFailed(ILogger logger, string stage, Exception exception);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Error, Message = "{Stage} did not stop")]
    public static partial void StageStopFailed(ILogger logger, string stage, Exception exception);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "Created {Path} with the default settings")]
    public static partial void SettingsCreated(ILogger logger, string path);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning,
        Message = "{Path} could not be read; using defaults without saving for this session")]
    public static partial void SettingsUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Warning,
        Message = "{Path} was invalid ({Reason}) and was reset to the defaults; original kept as {BackupPath}")]
    public static partial void SettingsReset(ILogger logger, string path, string? backupPath, string reason);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Warning,
        Message = "{Path} had invalid sections ({Reason}), which were reset to their defaults; original kept as {BackupPath}")]
    public static partial void SettingsRepaired(ILogger logger, string path, string? backupPath, string reason);

    [LoggerMessage(EventId = 5014, Level = LogLevel.Debug, Message = "Saved {Path}")]
    public static partial void SettingsSaved(ILogger logger, string path);

    [LoggerMessage(EventId = 5015, Level = LogLevel.Warning, Message = "Could not save {Path}; will retry with the next change")]
    public static partial void SettingsSaveFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 5016, Level = LogLevel.Warning, Message = "Could not back up {Path} before replacing it")]
    public static partial void SettingsBackupFailed(ILogger logger, string path, Exception exception);
}
