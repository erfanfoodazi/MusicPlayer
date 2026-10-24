namespace Domain.Models;

/// <summary>
/// A saved copy of the scanned library. Loading this at startup means the app does not have
/// to parse every audio file again on each launch; a lightweight folder check decides
/// whether a real rescan is still needed.
/// </summary>
public class LibrarySnapshot
{
    public int Version { get; set; } = 1;

    public string FolderPath { get; set; } = string.Empty;

    public DateTime SavedAtUtc { get; set; }

    public List<Music> Tracks { get; set; } = [];

    /// <summary>
    /// Every audio file the last scan saw, including files that could not be read (they carry
    /// no <see cref="Music"/> entry). The startup check compares this list with the folder so
    /// unreadable files do not look like "files were added" and trigger a pointless rescan.
    /// </summary>
    public List<string> FilePaths { get; set; } = [];

    /// <summary>
    /// User playlists. Track membership is stored as file paths (see
    /// <see cref="PlaylistTrack"/>): <see cref="Music.Id"/> values are reassigned on every
    /// load, so they are not stable across launches while a file path is.
    /// </summary>
    public List<Playlist> Playlists { get; set; } = [];
}
