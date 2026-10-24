using System.Collections.ObjectModel;
using Application.Services;
using Domain.Models;
using Infrastructure.Persistence;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Shared in-memory library. The Library page fills it from the persisted snapshot (or a
/// scan); Search and the detail pages read from the same instances, so every screen shows the
/// same tracks/albums/artists. Tag edits and deletions go through this service so the whole
/// UI updates consistently and the snapshot stays in sync.
/// </summary>
public sealed class LibraryService
{
    public ObservableCollection<Music> Tracks { get; } = [];
    public ObservableCollection<Album> Albums { get; } = [];
    public ObservableCollection<Artist> Artists { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];

    /// <summary>
    /// Every audio file the last scan found, including unreadable ones (a real scan reports
    /// those as errors). Kept so the saved snapshot can tell "files were added/removed" apart
    /// from "some files were never readable in the first place".
    /// </summary>
    private readonly List<string> _knownFilePaths = [];
    private readonly LibraryJsonStore _snapshotStore;

    public LibraryService(LibraryJsonStore snapshotStore)
    {
        _snapshotStore = snapshotStore;
    }

    public string? FolderPath { get; private set; }
    public DateTime? LastScanAt { get; private set; }

    public bool HasTracks => Tracks.Count > 0;

    public event EventHandler? LibraryChanged;

    public void ApplyScanResult(string folderPath, ScanResult result)
    {
        FolderPath = folderPath;
        LastScanAt = DateTime.Now;

        _knownFilePaths.Clear();
        _knownFilePaths.AddRange(result.Tracks.Select(t => t.FilePath));
        _knownFilePaths.AddRange(result.Errors.Select(e => e.FilePath));

        PublishTracks([.. result.Tracks]);
    }

    /// <summary>Restores a previously saved snapshot without touching the filesystem.</summary>
    public void ApplySnapshot(LibrarySnapshot snapshot)
    {
        FolderPath = snapshot.FolderPath;
        LastScanAt = snapshot.SavedAtUtc == DateTime.MinValue
            ? DateTime.Now
            : snapshot.SavedAtUtc.ToLocalTime();

        _knownFilePaths.Clear();
        _knownFilePaths.AddRange(snapshot.FilePaths.Count > 0
            ? snapshot.FilePaths
            : snapshot.Tracks.Select(t => t.FilePath));

        PublishTracks([.. snapshot.Tracks]);

        Playlists.Clear();
        foreach (var playlist in snapshot.Playlists)
        {
            if (!string.IsNullOrWhiteSpace(playlist.Name))
            {
                playlist.Name = NormalizePlaylistName(playlist.Name);
                Playlists.Add(playlist);
            }
        }

        // Repoints every stored file path at the live track instances.
        ResolvePlaylistTracks();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces one track after its file was re-read (tag edit).</summary>
    public void ReplaceTrack(Music oldTrack, Music newTrack)
    {
        var index = Tracks.IndexOf(oldTrack);
        if (index < 0)
        {
            return;
        }

        Tracks[index] = newTrack;
        PublishTracks([.. Tracks]);
    }

    /// <summary>Removes one track after its file was deleted from disk.</summary>
    public void RemoveTrack(Music track)
    {
        if (Tracks.Remove(track))
        {
            _knownFilePaths.RemoveAll(path => string.Equals(path, track.FilePath, StringComparison.OrdinalIgnoreCase));
            PublishTracks([.. Tracks]);
        }
    }

    /// <summary>Builds the object to persist next to the live library.</summary>
    public LibrarySnapshot CreateSnapshot() => new()
    {
        FolderPath = FolderPath ?? string.Empty,
        SavedAtUtc = LastScanAt?.ToUniversalTime() ?? DateTime.UtcNow,
        Tracks = [.. Tracks],
        FilePaths = [.. _knownFilePaths],

        // Deep-copy playlist entries. Saving happens off the UI thread while the user may
        // still add or remove tracks; sharing the live lists would let the enumerator fail
        // mid-write and drop the snapshot.
        Playlists = Playlists.Select(playlist => new Playlist
        {
            Id = playlist.Id,
            Name = playlist.Name,
            ArtworkPath = playlist.ArtworkPath,
            CreatedAt = playlist.CreatedAt,
            UpdatedAt = playlist.UpdatedAt,
            Tracks = playlist.Tracks.Select(entry => new PlaylistTrack
            {
                Id = entry.Id,
                PlaylistId = entry.PlaylistId,
                MusicId = entry.MusicId,
                Order = entry.Order,
                AddedAt = entry.AddedAt,
                Music = entry.Music,
            }).ToList(),
        }).ToList(),
    };

    // ------------------------------------------------------------- playlists

    /// <summary>Creates an empty playlist and persists it immediately.</summary>
    public Playlist CreatePlaylist(string name)
    {
        var playlist = new Playlist
        {
            Id = Playlists.Count == 0 ? 1 : Playlists.Max(p => p.Id) + 1,
            Name = NormalizePlaylistName(name),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        Playlists.Add(playlist);
        PersistPlaylists();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return playlist;
    }

    public bool RenamePlaylist(Playlist playlist, string name)
    {
        var trimmed = NormalizePlaylistName(name);
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(playlist.Name, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        playlist.Name = trimmed;
        playlist.UpdatedAt = DateTime.UtcNow;
        PersistPlaylists();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool DeletePlaylist(Playlist playlist)
    {
        if (!Playlists.Remove(playlist))
        {
            return false;
        }

        PersistPlaylists();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Adds a track unless it is already in the playlist. Returns true when changed.</summary>
    public bool AddTrackToPlaylist(Playlist playlist, Music track)
        => AddTracksToPlaylist(playlist, [track]) > 0;

    /// <summary>
    /// Appends a batch of songs in the order given, skipping duplicates. One snapshot write
    /// covers the whole batch, so adding a hundred tracks does not hammer the disk.
    /// Returns how many songs were actually added.
    /// </summary>
    public int AddTracksToPlaylist(Playlist playlist, IEnumerable<Music> tracks)
    {
        var knownPaths = new HashSet<string>(
            playlist.Tracks
                .Select(entry => entry.Music?.FilePath ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);

        var nextEntryId = playlist.Tracks.Count == 0 ? 1 : playlist.Tracks.Max(t => t.Id) + 1;
        var added = 0;

        foreach (var track in tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.FilePath) || !knownPaths.Add(track.FilePath))
            {
                continue;
            }

            playlist.Tracks.Add(new PlaylistTrack
            {
                Id = nextEntryId++,
                PlaylistId = playlist.Id,
                MusicId = track.Id,
                Order = playlist.Tracks.Count,
                AddedAt = DateTime.UtcNow,
                Music = track,
            });

            playlist.ArtworkPath ??= track.ArtworkPath ?? track.Album?.ArtworkPath;
            added++;
        }

        if (added == 0)
        {
            return 0;
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        PersistPlaylists();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public bool RemoveTrackFromPlaylist(Playlist playlist, Music track)
    {
        var entry = playlist.Tracks.FirstOrDefault(t =>
            ReferenceEquals(t.Music, track) ||
            string.Equals(t.Music?.FilePath ?? string.Empty, track.FilePath, StringComparison.OrdinalIgnoreCase));

        if (entry is null || !playlist.Tracks.Remove(entry))
        {
            return false;
        }

        for (var i = 0; i < playlist.Tracks.Count; i++)
        {
            playlist.Tracks[i].Order = i;
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        PersistPlaylists();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Repairs old names that were accidentally stored with URI escaping ("Night%20Drive")
    /// and collapses the duplicated suffix produced by an early playlist dialog.
    /// </summary>
    private static string NormalizePlaylistName(string name)
    {
        var value = name.Trim();

        if (value.Contains('%'))
        {
            try
            {
                value = Uri.UnescapeDataString(value).Trim();
            }
            catch
            {
                // A literal '%' is valid in a playlist name; keep the original text.
            }
        }

        if (value.Length > 2 && value.Length % 2 == 0)
        {
            var half = value.Length / 2;
            var first = value[..half];
            var second = value[half..];
            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                value = first.Trim();
            }
        }

        return value;
    }

    /// <summary>Fire-and-forget: a playlist edit must never block the UI thread.</summary>
    private void PersistPlaylists()
    {
        var snapshot = CreateSnapshot();
        _ = SaveSnapshotAsync(snapshot);
    }

    private async Task SaveSnapshotAsync(LibrarySnapshot snapshot)
    {
        try
        {
            await _snapshotStore.SaveAsync(snapshot);
        }
        catch
        {
            // Persisting is best effort; the in-memory library stays correct either way.
        }
    }

    /// <summary>
    /// Reconnects stored file paths to the live <see cref="Music"/> instances. Runs after
    /// every regrouping so edits and rescans cannot leave playlists pointing at dead objects.
    /// </summary>
    private void ResolvePlaylistTracks()
    {
        if (Playlists.Count == 0)
        {
            return;
        }

        var byPath = new Dictionary<string, Music>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.FilePath))
            {
                byPath[track.FilePath] = track;
            }
        }

        foreach (var playlist in Playlists)
        {
            playlist.Tracks = playlist.Tracks
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Music?.FilePath) &&
                                byPath.TryGetValue(entry.Music.FilePath, out _))
                .ToList();

            for (var i = 0; i < playlist.Tracks.Count; i++)
            {
                var entry = playlist.Tracks[i];
                entry.Music = byPath[entry.Music!.FilePath];
                entry.MusicId = entry.Music.Id;
                entry.PlaylistId = playlist.Id;
                entry.Order = i;
            }

            playlist.ArtworkPath ??= playlist.Tracks
                .Select(t => t.Music?.ArtworkPath ?? t.Music?.Album?.ArtworkPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }
    }

    /// <summary>
    /// Regroups the flat track list into shared Album/Artist instances and refills the
    /// observable collections. Used by scan results, snapshot restores and edits so all
    /// three paths produce identical indices.
    /// </summary>
    private void PublishTracks(List<Music> orderedTracks)
    {
        var grouped = LibraryGrouping.Build(orderedTracks);

        Tracks.Clear();
        foreach (var track in grouped)
        {
            Tracks.Add(track);
        }

        // The grouping helper builds shared Album/Artist instances, so Distinct() by
        // reference works.
        var albums = grouped
            .Select(t => t.Album)
            .Where(a => a is not null)
            .Distinct()
            .OrderBy(a => a!.Title, StringComparer.OrdinalIgnoreCase);

        Albums.Clear();
        foreach (var album in albums)
        {
            Albums.Add(album!);
        }

        var artists = grouped
            .Select(t => t.Artist)
            .Where(a => a is not null)
            .Distinct()
            .OrderBy(a => a!.Name, StringComparer.OrdinalIgnoreCase);

        Artists.Clear();
        foreach (var artist in artists)
        {
            Artists.Add(artist!);
        }

        // Playlists are added in the persistence phase.
        ResolvePlaylistTracks();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public Album? FindAlbum(int albumId) => Albums.FirstOrDefault(a => a.Id == albumId);

    public Artist? FindArtist(int artistId) => Artists.FirstOrDefault(a => a.Id == artistId);

    public Playlist? FindPlaylist(int playlistId) => Playlists.FirstOrDefault(p => p.Id == playlistId);
}
