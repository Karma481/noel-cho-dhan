namespace AmbientLight.Core.Threading;

/// <summary>An immutable value stamped with the version it was published under.</summary>
public sealed record VersionedValue<T>(T Value, long Version)
    where T : struct;

/// <summary>
/// A single-writer, many-reader cell for a small value that changes rarely, such as the content
/// bounds found by letterbox detection, which flow back from the processing stage to the capture stage.
/// </summary>
/// <remarks>
/// Readers take <see cref="Current"/> with one volatile read and compare its version with the last
/// one they applied, so they never lock and never observe a torn value (the value is boxed in an
/// immutable <see cref="VersionedValue{T}"/>, which also makes structs wider than 8 bytes safe).
/// <see cref="Publish"/> allocates only when the value actually changes; publishing an equal value is a
/// no-op, so a writer may call it on every frame without producing garbage.
/// </remarks>
public sealed class SnapshotCell<T>
    where T : struct, IEquatable<T>
{
    private VersionedValue<T> _current;

    /// <summary>Creates the cell holding <paramref name="initial"/> at version 0.</summary>
    public SnapshotCell(T initial)
    {
        _current = new VersionedValue<T>(initial, 0);
    }

    /// <summary>The latest value and its version. Safe from any thread.</summary>
    public VersionedValue<T> Current => Volatile.Read(ref _current);

    /// <summary>
    /// Writer side: publishes <paramref name="value"/> if it differs from the current one and returns
    /// <see langword="true"/> when a new version was published.
    /// </summary>
    public bool Publish(T value)
    {
        var current = _current;
        if (current.Value.Equals(value))
        {
            return false;
        }

        Volatile.Write(ref _current, new VersionedValue<T>(value, current.Version + 1));
        return true;
    }
}
