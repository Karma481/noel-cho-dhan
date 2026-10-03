namespace AmbientLight.Core.Settings;

/// <summary>How serious a settings validation finding is.</summary>
public enum SettingsIssueSeverity
{
    /// <summary>The pipeline runs, but a stated goal (for example latency) will not be met.</summary>
    Warning = 0,

    /// <summary>The pipeline must not start with these settings.</summary>
    Error = 1,
}

/// <summary>One validation finding, addressed by a dotted property path such as <c>serial.baudRate</c>.</summary>
public sealed record SettingsIssue(SettingsIssueSeverity Severity, string Path, string Message)
{
    /// <summary>Creates an error finding.</summary>
    public static SettingsIssue Error(string path, string message) => new(SettingsIssueSeverity.Error, path, message);

    /// <summary>Creates a warning finding.</summary>
    public static SettingsIssue Warning(string path, string message) => new(SettingsIssueSeverity.Warning, path, message);

    /// <inheritdoc />
    public override string ToString() => $"{Severity}: {Path}: {Message}";
}
