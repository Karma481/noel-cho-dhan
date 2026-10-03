using System.Numerics;

namespace AmbientLight.Processing.Pipeline;

/// <summary>
/// Frame-rate-independent exponential smoothing of zone colors in linear light.
/// </summary>
/// <remarks>
/// <para>
/// Each step moves the state towards the target by <c>alpha = 1 - exp(-dt / tau)</c>. Because alpha is
/// derived from the real elapsed time, the response is identical whether frames arrive at 30, 60 or
/// 144 Hz, or irregularly: after <c>tau</c> the state has covered 63% of a step change, after
/// <c>3 tau</c> 95%. This removes flicker from strobing content and makes scene cuts fade instead of jump.
/// </para>
/// <para>
/// Smoothing happens in linear light, so a fade between two colors passes through physically plausible
/// intermediate light levels. Buffers are allocated once for the maximum zone count.
/// </para>
/// </remarks>
internal sealed class TemporalSmoother
{
    /// <summary>
    /// Distance (max channel, linear) below which the state snaps to the target and smoothing is
    /// considered converged. 1e-4 is below one 8-bit step anywhere on the sRGB curve.
    /// </summary>
    internal const float ConvergenceEpsilon = 1e-4f;

    private readonly Vector3[] _state;
    private int _zoneCount;
    private bool _initialized;

    public TemporalSmoother(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _state = new Vector3[capacity];
    }

    /// <summary>Current smoothed colors.</summary>
    public ReadOnlySpan<Vector3> State => _state.AsSpan(0, _zoneCount);

    /// <summary>Jumps straight to <paramref name="target"/>, for example after the zone layout changed.</summary>
    public void Reset(ReadOnlySpan<Vector3> target)
    {
        EnsureCapacity(target.Length);
        target.CopyTo(_state);
        _zoneCount = target.Length;
        _initialized = true;
    }

    /// <summary>
    /// Advances the state towards <paramref name="target"/> by <paramref name="elapsedSeconds"/>.
    /// Returns <see langword="true"/> while the state has not yet converged (the caller should keep
    /// ticking even without new captures).
    /// </summary>
    public bool Advance(ReadOnlySpan<Vector3> target, float elapsedSeconds, float timeConstantSeconds)
    {
        if (!_initialized || target.Length != _zoneCount)
        {
            Reset(target);
            return false;
        }

        if (timeConstantSeconds <= 0f || !float.IsFinite(elapsedSeconds))
        {
            target.CopyTo(_state);
            return false;
        }

        var alpha = elapsedSeconds <= 0f ? 0f : 1f - MathF.Exp(-elapsedSeconds / timeConstantSeconds);
        var transitioning = false;
        var state = _state.AsSpan(0, _zoneCount);
        for (var i = 0; i < state.Length; i++)
        {
            var next = Vector3.Lerp(state[i], target[i], alpha);
            var remaining = Vector3.Abs(target[i] - next);
            if (MathF.Max(remaining.X, MathF.Max(remaining.Y, remaining.Z)) <= ConvergenceEpsilon)
            {
                next = target[i];
            }
            else
            {
                transitioning = true;
            }

            state[i] = next;
        }

        return transitioning;
    }

    private void EnsureCapacity(int zoneCount)
    {
        if (zoneCount > _state.Length)
        {
            throw new ArgumentException($"{zoneCount} zones exceed the smoother capacity of {_state.Length}.", nameof(zoneCount));
        }
    }
}
