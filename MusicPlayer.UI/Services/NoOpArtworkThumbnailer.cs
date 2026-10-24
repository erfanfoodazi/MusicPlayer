using Application.Services;
using Domain.Models;

namespace MusicPlayer.UI.Services;

/// <summary>Fallback used on platforms where the UI already handles large artwork efficiently.</summary>
public sealed class NoOpArtworkThumbnailer : IArtworkThumbnailer
{
    public void EnsureThumbnail(Artwork artwork, CancellationToken cancellationToken = default)
    {
    }
}
