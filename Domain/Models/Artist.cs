
namespace Domain.Models;

public class Artist
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Music> Tracks { get; set; } = [];

    public int TrackCount => Tracks.Count;

    public string TrackCountText => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";
}
