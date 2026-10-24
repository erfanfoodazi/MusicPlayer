using Domain.Models;

namespace Application.Services;

/// <summary>
/// Creates a small UI copy of extracted artwork. Implementations run during the background
/// scan, never on the UI thread, and must leave <see cref="Artwork.ThumbnailPath"/> null when
/// downscaling is unavailable so the UI can safely fall back to the original file.
/// </summary>
public interface IArtworkThumbnailer
{
    void EnsureThumbnail(Artwork artwork, CancellationToken cancellationToken = default);
}
