namespace AmbientLight.Host;

/// <summary>Where the app keeps its files: everything under one per-user folder, nothing next to the executable.</summary>
/// <param name="DataDirectory">Root folder, <c>%LOCALAPPDATA%\AmbientLight</c> by default.</param>
public sealed record AppPaths(string DataDirectory)
{
    /// <summary>The standard location.</summary>
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmbientLight"));

    /// <summary>The configuration file.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "config.json");

    /// <summary>Folder of the rolling log files.</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Log file name pattern; the logger inserts the date before the extension.</summary>
    public string LogFilePattern => Path.Combine(LogDirectory, "ambientlight-.log");
}
