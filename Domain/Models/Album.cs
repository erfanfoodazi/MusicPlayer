
namespace Domain.Models;

public class Album
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string AlbumArtist { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? ArtworkPath { get; set; }

    public List<Music> Tracks { get; set; } = [];

    public int TrackCount => Tracks.Count;
}
