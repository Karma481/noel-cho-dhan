namespace AmbientLight.Host.Platform;

/// <summary>Whether the app starts with Windows.</summary>
public enum StartupState
{
    /// <summary>No autostart entry exists.</summary>
    NotRegistered = 0,

    /// <summary>This executable starts at sign-in.</summary>
    Enabled = 1,

    /// <summary>The entry exists but was disabled in Task Manager's Startup apps page.</summary>
    DisabledInTaskManager = 2,

    /// <summary>The entry starts a different copy of the app (another folder).</summary>
    RegisteredElsewhere = 3,
}

/// <summary>
/// "Start with Windows" through the per-user <c>Run</c> key, the mechanism Task Manager's Startup apps page
/// understands, so the user can also manage it from there.
/// </summary>
/// <remarks>
/// <para>
/// Task Manager does not delete a disabled entry; it records the choice in <c>Explorer\StartupApproved\Run</c>
/// (a binary value whose first byte is even when enabled and odd when disabled). The state reported here
/// accounts for it, and turning the option on in the app clears that flag, because the user has just asked
/// for autostart explicitly.
/// </para>
/// <para>
/// The app is a single executable the user may move. <see cref="RepairIfMoved"/> re-points an entry whose
/// executable no longer exists at the current one, so autostart survives moving the file.
/// </para>
/// </remarks>
public sealed class StartupRegistration
{
    /// <summary>Per-user autostart key.</summary>
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager stores enabled/disabled flags for <see cref="RunKey"/> entries.</summary>
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Command-line switch passed at sign-in so the app starts in the tray without opening any window.</summary>
    public const string AutostartArgument = "--autostart";

    /// <summary>Value name of the entry.</summary>
    public const string DefaultValueName = "AmbientLight";

    // First byte 0x02 = enabled; the remaining 11 bytes are a FILETIME (zero when enabled).
    private static readonly byte[] EnabledFlag = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly IRegistryValueStore _registry;
    private readonly string _executablePath;
    private readonly string _valueName;
    private readonly Func<string, bool> _fileExists;

    /// <summary>Creates the registration for <paramref name="executablePath"/>.</summary>
    /// <param name="registry">HKCU access.</param>
    /// <param name="executablePath">Full path of this executable (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="valueName">Name of the Run value.</param>
    /// <param name="fileExists">File existence check; <see cref="File.Exists(string)"/> when <see langword="null"/>.</param>
    public StartupRegistration(
        IRegistryValueStore registry,
        string executablePath,
        string valueName = DefaultValueName,
        Func<string, bool>? fileExists = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _executablePath = executablePath;
        _valueName = valueName;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Current state of the autostart entry.</summary>
    public StartupState GetState()
    {
        if (_registry.GetValue(RunKey, _valueName) is not string command || command.Length == 0)
        {
            return StartupState.NotRegistered;
        }

        if (!PathsEqual(ParseExecutable(command), _executablePath))
        {
            return StartupState.RegisteredElsewhere;
        }

        return IsDisabledFlag(_registry.GetValue(ApprovedKey, _valueName))
            ? StartupState.DisabledInTaskManager
            : StartupState.Enabled;
    }

    /// <summary>True when this executable starts at sign-in.</summary>
    public bool IsEnabled => GetState() == StartupState.Enabled;

    /// <summary>Turns autostart on (for this executable) or off.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _registry.SetString(RunKey, _valueName, BuildCommand(_executablePath));
            if (_registry.GetValue(ApprovedKey, _valueName) is not null)
            {
                _registry.SetBinary(ApprovedKey, _valueName, EnabledFlag);
            }
        }
        else
        {
            _registry.DeleteValue(RunKey, _valueName);
            _registry.DeleteValue(ApprovedKey, _valueName);
        }
    }

    /// <summary>
    /// When the entry starts an executable that no longer exists (the file was moved), points it at this one.
    /// An entry for another copy that still exists is left alone. Returns <see langword="true"/> when it rewrote the entry.
    /// </summary>
    public bool RepairIfMoved()
    {
        if (_registry.GetValue(RunKey, _valueName) is not string command || command.Length == 0)
        {
            return false;
        }

        var registered = ParseExecutable(command);
        if (PathsEqual(registered, _executablePath) || (registered.Length > 0 && _fileExists(registered)))
        {
            return false;
        }

        _registry.SetString(RunKey, _valueName, BuildCommand(_executablePath));
        return true;
    }

    /// <summary>The Run value for <paramref name="executablePath"/>: the quoted path plus <see cref="AutostartArgument"/>.</summary>
    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" {AutostartArgument}";
    }

    /// <summary>Extracts the executable from a Run command line (quoted or not); empty when there is none.</summary>
    public static string ParseExecutable(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing < 0 ? trimmed[1..] : trimmed[1..closing];
        }

        // Unquoted: the path ends at the first ".exe" followed by the end or a space (paths may contain spaces).
        var extension = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (extension >= 0)
        {
            var end = extension + 4;
            if (end == trimmed.Length || trimmed[end] == ' ')
            {
                return trimmed[..end];
            }

            extension = trimmed.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase);
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>True when a <c>StartupApproved</c> value marks the entry as disabled (odd first byte).</summary>
    internal static bool IsDisabledFlag(object? approvedValue) =>
        approvedValue is byte[] { Length: > 0 } bytes && (bytes[0] & 0x01) != 0;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('/', '\\');
}
