namespace AmbientLight.Core.Frames;

/// <summary>
/// A pre-allocated buffer that can overwrite its whole content from another instance of the same type
/// without allocating. Required by <see cref="Threading.LatestValueBroadcaster{T}"/>.
/// </summary>
public interface ICopyFrom<in T>
{
    /// <summary>Overwrites this instance with the content of <paramref name="source"/>.</summary>
    void CopyFrom(T source);
}
