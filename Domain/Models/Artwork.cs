namespace Domain.Models;

/// <summary>
/// Describes one piece of album artwork. The image bytes themselves are never kept on this
/// model: embedded artwork is extracted to an app-cache file and <see cref="FilePath"/>
/// points at it, so lists can bind directly to a path without loading bytes into memory
/// (spec section 12: avoid loading every full-resolution artwork image at once).
/// </summary>
public class Artwork
{
    /// <summary>Local path to the extracted image file (usually inside the app cache directory).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional downscaled copy used by the player and album grids. Full-resolution artwork is
    /// kept in <see cref="FilePath"/> for the future editor, but UI never decodes it directly.
    /// </summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>
    /// Tiny copy (160 px) used by 40–60 px list rows. Decoding a 512 px cover for every row
    /// wastes CPU and memory and makes scrolling stutter on phones.
    /// </summary>
    public string? SmallThumbnailPath { get; set; }

    /// <summary>MIME type of the image, e.g. "image/jpeg".</summary>
    public string MimeType { get; set; } = "image/jpeg";

    /// <summary>Image file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Where this artwork came from, so future edits never silently overwrite a newer source.</summary>
    public ArtworkSource Source { get; set; } = ArtworkSource.Embedded;
}

public enum ArtworkSource
{
    /// <summary>Extracted from the audio file's embedded pictures.</summary>
    Embedded,

    /// <summary>Sidecar image file found next to the audio file (cover.jpg/folder.png).</summary>
    Folder,

    /// <summary>Chosen by the user in the artwork editor.</summary>
    Manual
}
