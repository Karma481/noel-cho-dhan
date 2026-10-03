using System.Collections.Immutable;
using AmbientLight.Core.Zones;

namespace AmbientLight.Core.Settings;

/// <summary>
/// An immutable, internally consistent view of the configuration: the settings, the zone layout derived
/// from them, and a version number that increases with every publish.
/// </summary>
/// <param name="Settings">The validated settings.</param>
/// <param name="Zones">Zones in strip (wire) order, derived from <see cref="AppSettings.LedLayout"/>.</param>
/// <param name="Version">Monotonically increasing; stamped into frames as their layout version.</param>
public sealed record SettingsSnapshot(AppSettings Settings, ImmutableArray<ZoneConfig> Zones, long Version);

/// <summary>
/// Publishes configuration to the pipeline threads without locks on the read side.
/// </summary>
/// <remarks>
/// Readers call <see cref="Current"/> once per iteration (a single volatile reference read) and use that
/// snapshot for the whole iteration. Writers (the UI thread, the settings file watcher) are serialized by
/// a private lock so versions are published in order; that lock is never touched by a pipeline thread.
/// </remarks>
public sealed class SettingsHolder
{
    private readonly Lock _publishLock = new();
    private SettingsSnapshot _current;

    /// <summary>Creates a holder with an initial configuration.</summary>
    /// <exception cref="ArgumentException">The settings contain errors.</exception>
    public SettingsHolder(AppSettings initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ThrowIfInvalid(initial);
        _current = new SettingsSnapshot(initial, ZoneLayoutBuilder.Build(initial.LedLayout), Version: 1);
    }

    /// <summary>The latest published snapshot.</summary>
    public SettingsSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised on the publishing thread after a new snapshot became current. Pipeline threads never subscribe
    /// (they poll <see cref="Current"/>); it is for the orchestrator and the UI.
    /// </summary>
    public event EventHandler<SettingsSnapshot>? Published;

    /// <summary>
    /// Validates and publishes new settings. Returns <see langword="false"/> (and publishes nothing)
    /// when validation reports an error; warnings are returned but do not block publishing.
    /// </summary>
    public bool TryPublish(AppSettings settings, out IReadOnlyList<SettingsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(settings);
        issues = settings.Validate();
        if (issues.Any(issue => issue.Severity == SettingsIssueSeverity.Error))
        {
            return false;
        }

        var zones = ZoneLayoutBuilder.Build(settings.LedLayout);
        SettingsSnapshot next;
        lock (_publishLock)
        {
            next = new SettingsSnapshot(settings, zones, _current.Version + 1);
            Volatile.Write(ref _current, next);
        }

        Published?.Invoke(this, next);
        return true;
    }

    private static void ThrowIfInvalid(AppSettings settings)
    {
        var errors = settings.Validate()
            .Where(issue => issue.Severity == SettingsIssueSeverity.Error)
            .Select(issue => issue.ToString())
            .ToArray();
        if (errors.Length > 0)
        {
            throw new ArgumentException("Invalid settings: " + string.Join("; ", errors), nameof(settings));
        }
    }
}
