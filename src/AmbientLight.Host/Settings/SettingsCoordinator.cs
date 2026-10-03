using System.Globalization;
using AmbientLight.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AmbientLight.Host.Settings;

/// <summary>How the configuration file was found at startup.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>The file was read and is valid.</summary>
    Loaded = 0,

    /// <summary>There was no file (first run); one was written with the defaults.</summary>
    CreatedDefaults = 1,

    /// <summary>Some sections were invalid and were reset to their defaults; the original file was backed up.</summary>
    Repaired = 2,

    /// <summary>The file was not valid JSON settings; it was backed up and replaced with the defaults.</summary>
    ResetToDefaults = 3,

    /// <summary>
    /// The file exists but could not be read (locked, no permission). Defaults are used for this session and
    /// autosave is off, so the unreadable file is never overwritten.
    /// </summary>
    Unreadable = 4,
}

/// <summary>Result of <see cref="SettingsCoordinator.Load"/>.</summary>
/// <param name="Settings">Settings to start with; always valid.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="BackupPath">Where the original file was copied before it was rewritten, if it was.</param>
/// <param name="Issues">Validation findings of the file as it was read (errors that caused a repair, or warnings).</param>
public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadOutcome Outcome,
    string? BackupPath,
    IReadOnlyList<SettingsIssue> Issues);

/// <summary>
/// Owns <c>config.json</c>: loads (and repairs) it at startup, then saves every published change after a short
/// quiet period, so dragging a slider writes the file once instead of on every step.
/// </summary>
/// <remarks>
/// Saves run on a timer thread and replace the file atomically (<see cref="AppSettingsStore.Save"/>). A failed
/// save is logged and retried with the next change or at <see cref="Flush"/>; it never throws into the UI.
/// </remarks>
public sealed class SettingsCoordinator : IDisposable
{
    /// <summary>Quiet period after the last change before the file is written.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<SettingsCoordinator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly ITimer _timer;
    private readonly Lock _pendingLock = new();
    private readonly Lock _saveLock = new();

    private AppSettings? _pending;
    private SettingsHolder? _holder;
    private string? _lastSaveError;
    private volatile bool _autosaveEnabled = true;
    private bool _disposed;

    /// <summary>Creates the coordinator for the file at <paramref name="path"/>.</summary>
    /// <param name="path">Location of <c>config.json</c>.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="timeProvider">Clock and timers; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <param name="debounce">Quiet period before saving; <see cref="DefaultDebounce"/> when <see langword="null"/>.</param>
    public SettingsCoordinator(string path, ILogger<SettingsCoordinator> logger, TimeProvider? timeProvider = null, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _timer = _timeProvider.CreateTimer(
            static state => ((SettingsCoordinator)state!).SavePending(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Location of the configuration file.</summary>
    public string Path { get; }

    /// <summary>False when the file could not be read at startup, so it must not be overwritten.</summary>
    public bool AutosaveEnabled => _autosaveEnabled;

    /// <summary>Message of the latest failed save, or <see langword="null"/> after a successful one.</summary>
    public string? LastSaveError => Volatile.Read(ref _lastSaveError);

    /// <summary>
    /// Reads the configuration. Never throws for a bad file: invalid sections are reset to their defaults,
    /// an unparsable file is replaced with the defaults, and in both cases the original is backed up first.
    /// </summary>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            var defaults = new AppSettings();
            TrySave(defaults);
            HostLog.SettingsCreated(_logger, Path);
            return new SettingsLoadResult(defaults, SettingsLoadOutcome.CreatedDefaults, null, []);
        }

        string json;
        try
        {
            json = File.ReadAllText(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _autosaveEnabled = false;
            HostLog.SettingsUnreadable(_logger, Path, exception);
            return new SettingsLoadResult(new AppSettings(), SettingsLoadOutcome.Unreadable, null, []);
        }

        AppSettings parsed;
        try
        {
            parsed = AppSettingsStore.Deserialize(json);
        }
        catch (InvalidDataException exception)
        {
            var backup = TryBackup();
            var defaults = new AppSettings();
            TrySave(defaults);
            HostLog.SettingsReset(_logger, Path, backup, exception.Message);
            return new SettingsLoadResult(defaults, SettingsLoadOutcome.ResetToDefaults, backup, [SettingsIssue.Error("$", exception.Message)]);
        }

        var issues = parsed.Validate();
        if (!issues.Any(static issue => issue.Severity == SettingsIssueSeverity.Error))
        {
            return new SettingsLoadResult(parsed, SettingsLoadOutcome.Loaded, null, issues);
        }

        var backupPath = TryBackup();
        var repaired = ResetInvalidSections(parsed, issues);
        if (repaired.Validate().Any(static issue => issue.Severity == SettingsIssueSeverity.Error))
        {
            var defaults = new AppSettings();
            TrySave(defaults);
            HostLog.SettingsReset(_logger, Path, backupPath, string.Join("; ", issues));
            return new SettingsLoadResult(defaults, SettingsLoadOutcome.ResetToDefaults, backupPath, issues);
        }

        TrySave(repaired);
        HostLog.SettingsRepaired(_logger, Path, backupPath, string.Join("; ", issues.Where(static issue => issue.Severity == SettingsIssueSeverity.Error)));
        return new SettingsLoadResult(repaired, SettingsLoadOutcome.Repaired, backupPath, issues);
    }

    /// <summary>Starts saving every snapshot <paramref name="holder"/> publishes.</summary>
    public void Attach(SettingsHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_holder is not null)
        {
            throw new InvalidOperationException("Already attached to a settings holder.");
        }

        _holder = holder;
        holder.Published += OnPublished;
    }

    /// <summary>Writes a pending change now (on exit, or before the settings file is opened in an editor).</summary>
    public void Flush()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SavePending();
    }

    /// <summary>Stops following the holder after writing any pending change.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_holder is not null)
        {
            _holder.Published -= OnPublished;
        }

        Flush();
        _timer.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Replaces each section that has a validation error with its defaults, keeping the valid sections, so one
    /// mistyped value in a hand-edited file does not throw away the rest of the configuration.
    /// </summary>
    internal static AppSettings ResetInvalidSections(AppSettings settings, IEnumerable<SettingsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(issues);

        foreach (var issue in issues)
        {
            if (issue.Severity != SettingsIssueSeverity.Error)
            {
                continue;
            }

            var separator = issue.Path.IndexOf('.', StringComparison.Ordinal);
            var section = separator < 0 ? issue.Path : issue.Path[..separator];
            settings = section switch
            {
                "schemaVersion" => settings with { SchemaVersion = AppSettings.CurrentSchemaVersion },
                "capture" => settings with { Capture = new CaptureSettings() },
                "processing" => settings with { Processing = new ProcessingSettings() },
                "overlay" => settings with { Overlay = new OverlaySettings() },
                "serial" => settings with { Serial = new SerialSettings() },
                "ledLayout" => settings with { LedLayout = new LedLayoutSettings() },
                "letterbox" => settings with { Letterbox = new LetterboxSettings() },
                "performance" => settings with { Performance = new PerformanceSettings() },
                _ => new AppSettings(),
            };
        }

        return settings;
    }

    private void OnPublished(object? sender, SettingsSnapshot snapshot)
    {
        lock (_pendingLock)
        {
            _pending = snapshot.Settings;
        }

        // Restarting the one-shot timer on every change is what makes the save wait for a quiet period.
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void SavePending()
    {
        lock (_saveLock)
        {
            AppSettings? settings;
            lock (_pendingLock)
            {
                settings = _pending;
                _pending = null;
            }

            if (settings is null || !_autosaveEnabled)
            {
                return;
            }

            if (!TrySave(settings))
            {
                // Keep it for the next attempt unless a newer change has arrived meanwhile.
                lock (_pendingLock)
                {
                    _pending ??= settings;
                }
            }
        }
    }

    private bool TrySave(AppSettings settings)
    {
        try
        {
            AppSettingsStore.Save(Path, settings);
            Volatile.Write(ref _lastSaveError, null);
            HostLog.SettingsSaved(_logger, Path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _lastSaveError, exception.Message);
            HostLog.SettingsSaveFailed(_logger, Path, exception);
            return false;
        }
    }

    private string? TryBackup()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".";
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        var stamp = _timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = System.IO.Path.Combine(directory, $"{name}.invalid-{stamp}.json");
        for (var attempt = 2; File.Exists(backup); attempt++)
        {
            backup = System.IO.Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{name}.invalid-{stamp}-{attempt}.json"));
        }

        try
        {
            File.Copy(Path, backup);
            return backup;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HostLog.SettingsBackupFailed(_logger, Path, exception);
            return null;
        }
    }
}
