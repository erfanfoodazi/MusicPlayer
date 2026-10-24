using Domain.Models;

namespace Domain.Interfaces;

/// <summary>
/// Writes user-edited metadata back into an audio file (spec section 7). Implementations
/// must save atomically enough that a failed write never corrupts the file, and must throw
/// a meaningful exception when the platform or the file's permissions refuse the write.
/// </summary>
public interface IMetadataWriter
{
    /// <summary>
    /// Applies <paramref name="edit"/> to the tags of <paramref name="filePath"/>. Only
    /// non-null fields are changed. Throws <see cref="InvalidOperationException"/> when the
    /// file cannot be written (locked, read-only, or missing platform permission).
    /// </summary>
    void Write(string filePath, MusicTagEdit edit);
}
