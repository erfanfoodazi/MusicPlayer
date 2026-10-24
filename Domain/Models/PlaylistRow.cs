namespace Domain.Models;

/// <summary>One row of the Lists grid: two playlists side by side.</summary>
public class PlaylistRow
{
    public Playlist? Left { get; set; }
    public Playlist? Right { get; set; }
}
