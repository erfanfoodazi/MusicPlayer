using Domain.Models;

namespace Domain.Interfaces;

/// <summary>
/// Reads tags and technical information from an audio file.
///
/// Domain depends on this interface rather than on TagLibSharp directly, so the tagging
/// library can be replaced or mocked without touching business logic (spec section 10:
/// keep domain models independent of low-level metadata libraries).
/// </summary>
public interface IMetadataReader
{
    /// <summary>
    /// Reads metadata from a file on a real filesystem path (Windows).
    /// Throws on unreadable/corrupt files - callers decide how to report the error.
    /// </summary>
    Music Read(string filePath);

    /// <summary>
    /// Reads metadata from a seekable stream. Used on platforms where files are exposed as
    /// content streams rather than paths (Android content URIs - next step).
    /// </summary>
    /// <param name="stream">A seekable, readable stream positioned anywhere; the reader rewinds it.</param>
    /// <param name="nameHint">File name including extension (e.g. "song.mp3") - identifies the format.</param>
    Music Read(Stream stream, string nameHint);
}
