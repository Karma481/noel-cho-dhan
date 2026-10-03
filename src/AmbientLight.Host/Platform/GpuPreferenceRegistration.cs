using System.Globalization;

namespace AmbientLight.Host.Platform;

/// <summary>Outcome of <see cref="GpuPreferenceRegistration.EnsurePowerSaving"/>.</summary>
public enum GpuPreferenceOutcome
{
    /// <summary>The app was registered for the power-saving GPU.</summary>
    Registered = 0,

    /// <summary>The power-saving GPU was already selected.</summary>
    AlreadyPowerSaving = 1,

    /// <summary>The user chose another GPU (or "Let Windows decide") in Windows Settings; the choice is kept.</summary>
    UserChoiceKept = 2,
}

/// <summary>
/// The per-app GPU preference of Windows Settings &gt; System &gt; Display &gt; Graphics, stored as
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c> → <c>"&lt;exe path&gt;" = "GpuPreference=1;"</c>.
/// </summary>
/// <remarks>
/// <para>
/// On a hybrid laptop the built-in panel is wired to the integrated GPU. An app that runs on the discrete GPU
/// gets <c>DXGI_ERROR_UNSUPPORTED</c> from Desktop Duplication, and its overlay frames have to be copied across
/// adapters. Preferring the power-saving GPU avoids both and saves battery. The capture stage also copes
/// without this entry (it enumerates adapters in minimum-power order and moves to another adapter after
/// UNSUPPORTED); the entry makes the right choice the default for every DXGI and Direct3D call of the process.
/// Windows applies it at the next process start.
/// </para>
/// <para>
/// The value may hold other settings (<c>SwapEffectUpgradeEnable=1;</c>, ...), which are preserved. A
/// <c>GpuPreference</c> the user set is never changed.
/// </para>
/// </remarks>
public sealed class GpuPreferenceRegistration
{
    /// <summary>Per-app GPU preference key.</summary>
    internal const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>GpuPreference value for "Power saving".</summary>
    public const int PowerSaving = 1;

    private const string PreferenceName = "GpuPreference";

    private readonly IRegistryValueStore _registry;
    private readonly string _executablePath;

    /// <summary>Creates the registration for <paramref name="executablePath"/> (<see cref="Environment.ProcessPath"/>).</summary>
    public GpuPreferenceRegistration(IRegistryValueStore registry, string executablePath)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <summary>The preference currently stored for this executable, or <see langword="null"/> when none is.</summary>
    public int? GetPreference() => ParsePreference(_registry.GetValue(Key, _executablePath) as string);

    /// <summary>Registers the power-saving GPU unless a preference is already set.</summary>
    public GpuPreferenceOutcome EnsurePowerSaving()
    {
        var existing = _registry.GetValue(Key, _executablePath) as string;
        if (!HasPreferenceEntry(existing))
        {
            _registry.SetString(Key, _executablePath, WithPreference(existing, PowerSaving));
            return GpuPreferenceOutcome.Registered;
        }

        // Any existing entry, even one this code cannot parse, is the user's (or Windows') choice.
        return ParsePreference(existing) == PowerSaving
            ? GpuPreferenceOutcome.AlreadyPowerSaving
            : GpuPreferenceOutcome.UserChoiceKept;
    }

    /// <summary>Reads <c>GpuPreference=N</c> from a <c>name=value;</c> list; <see langword="null"/> when absent or malformed.</summary>
    public static int? ParsePreference(string? data) =>
        FindPreferenceValue(data) is { } text &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>True when the list contains a <c>GpuPreference</c> entry, whatever its value.</summary>
    internal static bool HasPreferenceEntry(string? data) => FindPreferenceValue(data) is not null;

    private static string? FindPreferenceValue(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        foreach (var entry in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && entry[..separator].Trim().Equals(PreferenceName, StringComparison.OrdinalIgnoreCase))
            {
                return entry[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Appends <c>GpuPreference=<paramref name="preference"/>;</c> to an existing list, keeping its other entries.</summary>
    internal static string WithPreference(string? existing, int preference)
    {
        var entry = string.Create(CultureInfo.InvariantCulture, $"{PreferenceName}={preference};");
        if (string.IsNullOrWhiteSpace(existing))
        {
            return entry;
        }

        var trimmed = existing.Trim();
        return trimmed.EndsWith(';') ? trimmed + entry : trimmed + ";" + entry;
    }
}
