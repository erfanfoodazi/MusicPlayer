using Domain.Models;

namespace Application.Services;

/// <summary>
/// Builds the shared Artist/Album instances the library UI groups by. Both the scanner and
/// the in-app editors use this, so a tag edit or a deletion re-groups the library exactly
/// the same way a fresh scan would.
/// </summary>
public static class LibraryGrouping
{
    /// <summary>
    /// Groups <paramref name="tracks"/> in place: assigns stable in-session IDs, creates
    /// shared <see cref="Artist"/> and <see cref="Album"/> instances, and repoints every
    /// track at them. Album identity = title + album artist, because two different artists
    /// can release albums with the same title.
    /// </summary>
    public static List<Music> Build(List<Music> tracks)
    {
        var artistsByName = new Dictionary<string, Artist>(StringComparer.OrdinalIgnoreCase);
        var albumsByKey = new Dictionary<string, Album>(StringComparer.OrdinalIgnoreCase);
        var nextId = 1;

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];

            // The in-memory library has no database; assign stable IDs so navigation
            // (albumId/artistId) and selection can identify items within this session.
            track.Id = i + 1;

            var artistName = track.Artist?.Name;
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                if (!artistsByName.TryGetValue(artistName, out var artist))
                {
                    artist = track.Artist ?? new Artist { Name = artistName };
                    artist.Id = nextId++;
                    artist.Name = artistName;
                    artist.Tracks.Clear();
                    artistsByName[artistName] = artist;
                }

                artist.Tracks.Add(track);
                track.Artist = artist; // point every track at the shared instance
                track.ArtistId = artist.Id;
            }
            else
            {
                track.Artist = null;
                track.ArtistId = null;
            }

            var albumTitle = track.Album?.Title;
            if (!string.IsNullOrWhiteSpace(albumTitle))
            {
                var key = $"{albumTitle}\u0000{track.AlbumArtist}";

                if (!albumsByKey.TryGetValue(key, out var album))
                {
                    album = track.Album ?? new Album { Title = albumTitle };
                    album.Id = nextId++;
                    album.Title = albumTitle;
                    album.AlbumArtist = track.AlbumArtist;
                    album.Tracks.Clear();
                    albumsByKey[key] = album;
                }

                if (!album.Tracks.Contains(track))
                {
                    album.Tracks.Add(track);
                }

                // Promote the first available cover to the album so the Albums tab
                // shows artwork immediately (extracted file path), then the thumbnail
                // path once the scanner downsizes it a few seconds later.
                if (string.IsNullOrEmpty(album.ArtworkPath))
                {
                    album.ArtworkPath = track.ArtworkPath;
                }

                // Promote the first available cover to the album so the Albums tab
                // shows artwork immediately (extracted file path), then the thumbnail
                // path once the scanner downsizes it a few seconds later.
                if (string.IsNullOrEmpty(album.ArtworkPath))
                {
                    album.ArtworkPath = track.ArtworkPath;
                }

                track.Album = album; // point every track at the shared instance
                track.AlbumId = album.Id;
            }
            else
            {
                track.Album = null;
                track.AlbumId = null;
            }
        }

        return tracks;
    }
}
