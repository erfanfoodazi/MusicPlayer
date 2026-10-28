using System.Collections.ObjectModel;
using Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using Infrastructure.Persistence;
using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class LibraryViewModel : BaseViewModel, IPlayTrackContext
{
    System.Windows.Input.ICommand IPlayTrackContext.PlayTrackCommand => PlayTrackCommand;
    System.Windows.Input.ICommand IPlayTrackContext.OpenTrackCommand => OpenTrackCommand;

    private const string LastFolderPreferenceKey = "library.lastFolder";
    private const string TrackSortKey = "library.sort.tracks";
    private const string AlbumSortKey = "library.sort.albums";
    private const string ArtistSortKey = "library.sort.artists";
    private const string PlaylistSortKey = "library.sort.playlists";

    private readonly PlayerService _playerService;
    private readonly MusicLibraryScanner _scanner;
    private readonly IFolderPicker _folderPicker;
    private readonly IMusicFileDiscovery _fileDiscovery;
    private readonly LibraryService _library;
    private readonly LibraryJsonStore _snapshotStore;
    private readonly IArtworkThumbnailer _artworkThumbnailer;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _recentsSaveCts;
    private bool _artworkUpgradeStarted;
    private bool _refreshRunning;
    private int _lastTrackCount = -1;
    private string _trackSort;
    private string _albumSort;
    private string _artistSort;
    private string _playlistSort;

    [ObservableProperty]
    private ObservableCollection<Music> _tracks = [];

    [ObservableProperty]
    private ObservableCollection<Album> _albums = [];

    [ObservableProperty]
    private ObservableCollection<AlbumRow> _albumRows = [];

    [ObservableProperty]
    private ObservableCollection<Artist> _artists = [];

    [ObservableProperty]
    private ObservableCollection<Playlist> _playlists = [];

    [ObservableProperty]
    private ObservableCollection<PlaylistRow> _playlistRows = [];

    [ObservableProperty]
    private ObservableCollection<Music> _recentlyPlayed = [];

    [ObservableProperty]
    private ObservableCollection<Music> _recentlyAdded = [];

    [ObservableProperty]
    private bool _showRecentlyPlayed;

    [ObservableProperty]
    private bool _showRecentlyAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTracks))]
    [NotifyPropertyChangedFor(nameof(TrackCountText))]
    [NotifyPropertyChangedFor(nameof(TabCountText))]
    private int _visibleTrackCount;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool HasTracks => VisibleTrackCount > 0;

    public string TrackCountText => VisibleTrackCount == 1 ? "1 track" : $"{VisibleTrackCount} tracks";

    public string TabCountText => SelectedTabIndex switch
    {
        0 => TrackCountText,
        1 => Albums.Count == 1 ? "1 album" : $"{Albums.Count} albums",
        2 => Artists.Count == 1 ? "1 artist" : $"{Artists.Count} artists",
        _ => Playlists.Count == 1 ? "1 playlist" : $"{Playlists.Count} playlists"
    };

    public string LibrarySummary
    {
        get
        {
            var tracks = _library.Tracks.Count;
            var albums = _library.Albums.Count;
            var artists = _library.Artists.Count;

            if (tracks == 0)
            {
                return "Your music, beautifully organized";
            }

            var albumText = albums == 1 ? "1 album" : $"{albums} albums";
            var artistText = artists == 1 ? "1 artist" : $"{artists} artists";
            return $"{tracks} songs · {albumText} · {artistText}";
        }
    }

    /// <summary>Short label shown on the sort chip for the active tab.</summary>
    public string SortDisplayName => SelectedTabIndex switch
    {
        0 => _trackSort switch
        {
            "title_desc" => "Title ↓",
            "artist" => "Artist",
            "album" => "Album",
            "year" => "Year",
            "duration" => "Duration",
            "modified" => "Modified",
            "recent" => "Recent",
            _ => "Title"
        },
        1 => _albumSort switch
        {
            "artist" => "Artist",
            "year" => "Year",
            _ => "Title"
        },
        2 => _artistSort switch
        {
            "songs" => "Songs",
            _ => "Name"
        },
        _ => _playlistSort switch
        {
            "updated" => "Recent",
            "songs" => "Songs",
            _ => "Name"
        }
    };

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsTracksTab => SelectedTabIndex == 0;
    public bool IsAlbumsTab => SelectedTabIndex == 1;
    public bool IsArtistsTab => SelectedTabIndex == 2;
    public bool IsPlaylistsTab => SelectedTabIndex == 3;

    public LibraryViewModel(
        PlayerService playerService,
        MusicLibraryScanner scanner,
        IFolderPicker folderPicker,
        IMusicFileDiscovery fileDiscovery,
        LibraryService library,
        LibraryJsonStore snapshotStore,
        IArtworkThumbnailer artworkThumbnailer)
    {
        _playerService = playerService;
        _scanner = scanner;
        _folderPicker = folderPicker;
        _fileDiscovery = fileDiscovery;
        _library = library;
        _snapshotStore = snapshotStore;
        _artworkThumbnailer = artworkThumbnailer;
        Title = "Library";

        _trackSort = Preferences.Default.Get(TrackSortKey, "title");
        _albumSort = Preferences.Default.Get(AlbumSortKey, "title");
        _artistSort = Preferences.Default.Get(ArtistSortKey, "name");
        _playlistSort = Preferences.Default.Get(PlaylistSortKey, "name");

        _library.LibraryChanged += OnLibraryChanged;
        _playerService.PropertyChanged += OnPlayerPropertyChanged;
        SyncFromLibrary();
    }

    /// <summary>
    /// The player stamps LastPlayedAt as a track starts. Refresh the shelf in place and save
    /// the snapshot a few seconds later so short skips only cost one write.
    /// </summary>
    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerService.CurrentTrack))
        {
            return;
        }

        if (MainThread.IsMainThread)
        {
            HandleTrackStarted();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(HandleTrackStarted);
        }
    }

    private void HandleTrackStarted()
    {
        UpdateRecents(showShelves: !HasSearchText);
        ScheduleRecentsSave();
    }

    private void ScheduleRecentsSave()
    {
        _recentsSaveCts?.Cancel();
        _recentsSaveCts?.Dispose();
        _recentsSaveCts = new CancellationTokenSource();
        _ = SaveRecentsAsync(_recentsSaveCts.Token);
    }

    private async Task SaveRecentsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(3000, token);

            if (IsBusy)
            {
                // A running scan persists its own snapshot when it finishes.
                return;
            }

            await _snapshotStore.SaveAsync(_library.CreateSnapshot());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missed save only costs the shelf until the next play.
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            SyncFromLibrary();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(SyncFromLibrary);
        }
    }

    /// <summary>
    /// Loads on first appearance. A saved snapshot is restored instantly (no audio file is
    /// parsed); only when the folder's file set changed does a real scan run in the
    /// background. The explicit Rescan command always scans.
    /// </summary>
    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (_library.Tracks.Count > 0)
        {
            SyncFromLibrary();
            return;
        }

        var snapshot = await _snapshotStore.LoadAsync();

        if (snapshot is { Tracks.Count: > 0 })
        {
            _library.ApplySnapshot(snapshot);
            SyncFromLibrary();

            if (!string.IsNullOrWhiteSpace(snapshot.FolderPath))
            {
                Preferences.Default.Set(LastFolderPreferenceKey, snapshot.FolderPath);
            }

            // Snapshots created before small thumbnails existed still point every list row
            // at a 512 px cover. Generate the tiny copies in the background and persist them;
            // newly realized rows pick them up immediately, the next launch uses them for all.
            StartArtworkUpgrade();

            // Quietly compare the folder contents with the snapshot; only added or removed
            // files trigger a rescan.
            _ = VerifyAgainstFolderAsync(snapshot);
            return;
        }

        var folder = Preferences.Default.Get(LastFolderPreferenceKey, string.Empty);

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = MusicLocations.GetDefaultFolder() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Preferences.Default.Set(LastFolderPreferenceKey, folder);
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusMessage = "No music folder found. Tap Scan to choose one.";
            return;
        }

        if (!Directory.Exists(folder))
        {
            StatusMessage = $"Saved folder is no longer accessible: {folder}";
            return;
        }

        await ScanFolderAsync(folder);
    }

    private void StartArtworkUpgrade()
    {
        if (_artworkUpgradeStarted || IsBusy)
        {
            return;
        }

        var artworks = _library.Tracks
            .Select(track => track.Artwork)
            .Where(artwork => artwork is not null &&
                              string.IsNullOrEmpty(artwork.SmallThumbnailPath) &&
                              !string.IsNullOrWhiteSpace(artwork.FilePath))
            .DistinctBy(artwork => artwork!.ThumbnailPath ?? artwork.FilePath)
            .ToList();

        if (artworks.Count == 0)
        {
            return;
        }

        _artworkUpgradeStarted = true;

        _ = Task.Run(async () =>
        {
            try
            {
                // Let the first frames, list realization and any startup scan settle before
                // decoding covers. Doing this immediately made Debug builds feel frozen.
                await Task.Delay(2500).ConfigureAwait(false);

                foreach (var artwork in artworks)
                {
                    _artworkThumbnailer.EnsureThumbnail(artwork!);
                }

                // Once thumbnails exist, promote each album's cover path to the downscaled
                // thumbnail and rebind the Albums tab so covers appear (and stay sharp).
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    // Promote every album's cover to the downscaled thumbnail so the
                    // Albums tab shows sharp covers (not the placeholder). Setting
                    // ArtworkPath raises PropertyChanged so each tile rebinds.
                    var updated = 0;
                    for (var i = 0; i < Albums.Count; i++)
                    {
                        var album = Albums[i];
                        var thumb = album.Tracks
                            .Select(t => t.Artwork?.ThumbnailPath)
                            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

                        if (!string.IsNullOrWhiteSpace(thumb) && album.ArtworkPath != thumb)
                        {
                            album.ArtworkPath = thumb;
                            updated++;
                        }
                    }

                    // Replace the collection so the CollectionView re-realizes every tile
                    // with the new cover paths.
                    Albums = new ObservableCollection<Album>(Albums);

                    if (updated > 0)
                    {
                        StatusMessage = $"Covers ready for {updated} album{(updated == 1 ? "" : "s")}.";
                    }

                    return _snapshotStore.SaveAsync(_library.CreateSnapshot());
                });
            }
            catch
            {
                // A missing or unreadable cover is cosmetic; the extracted file path still works.
            }
        });
    }

    /// <summary>
    /// Compares the saved file list against the folder (cheap directory walk, no metadata
    /// parsing). Runs off the UI thread and only starts a scan when files were added/removed.
    /// </summary>
    private async Task VerifyAgainstFolderAsync(LibrarySnapshot snapshot)
    {
        try
        {
            var folder = snapshot.FolderPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return;
            }

            IReadOnlyList<string> found;
            if (_fileDiscovery.ScansWholeLibrary)
            {
                found = await _fileDiscovery.DiscoverAsync(folder, CancellationToken.None);
                if (found.Count == 0)
                {
                    // Media permission may still be pending, or the index is temporarily
                    // empty. Keep the saved library instead of wiping it.
                    return;
                }
            }
            else
            {
                found = await Task.Run(
                    () => MusicLibraryScanner.EnumerateAudioFiles(folder, CancellationToken.None));
            }

            // The snapshot lists every file the last scan saw - including unreadable ones -
            // so only files actually added or removed start a rescan. Snapshots written by an
            // older build have no file list; those rescan once and save the fuller snapshot.
            var savedSource = snapshot.FilePaths.Count > 0
                ? snapshot.FilePaths
                : snapshot.Tracks.Select(t => t.FilePath);

            var savedPaths = savedSource
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var foundPaths = found
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!savedPaths.SequenceEqual(foundPaths, StringComparer.OrdinalIgnoreCase))
            {
                await ScanFolderAsync(folder);
            }
        }
        catch (Exception ex)
        {
            // The startup check must never take the app down; a failed check just means the
            // snapshot is used as-is until the user rescans.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Pull-to-refresh is deliberately a fast, side-effect-free reload. A full rescan parses
    /// every tag and cover and can take several seconds on a large library; keeping that work
    /// inside RefreshView left the native spinner running and made the list look frozen. The
    /// toolbar Rescan button (and folder picker) still performs the full scan.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        // RefreshView sets IsRefreshing to true before it invokes this command. Returning
        // early on that flag (the old behaviour) left the native spinner running forever.
        if (_refreshRunning)
        {
            return;
        }

        _refreshRunning = true;
        IsRefreshing = true;

        try
        {
            // Reapply search/sort and pick up playlist edits without touching disk.
            SyncFromLibrary();
            StatusMessage = _library.Tracks.Count == 0
                ? "No tracks loaded. Tap Rescan to choose a folder."
                : string.Empty;

            // A short hold gives the pull gesture a visible response, then the spinner closes
            // even if the user keeps dragging.
            await Task.Delay(220);
        }
        finally
        {
            _refreshRunning = false;
            IsRefreshing = false;
        }
    }

    /// <summary>Forces a rescan of the remembered folder or the whole Android media index.</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        var folder = Preferences.Default.Get(LastFolderPreferenceKey, string.Empty);

        if (_fileDiscovery.ScansWholeLibrary)
        {
            await ScanFolderAsync(string.IsNullOrWhiteSpace(folder) ? _fileDiscovery.SourcePath : folder);
            return;
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            await PickFolderAsync();
            return;
        }

        await ScanFolderAsync(folder);
    }

    /// <summary>Shows the platform folder picker, remembers the choice, then scans it.</summary>
    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = await _folderPicker.PickFolderAsync();

        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusMessage = "No folder selected. On Android, copy music into the Music folder and try again.";
            return;
        }

        Preferences.Default.Set(LastFolderPreferenceKey, folder);
        await ScanFolderAsync(folder);
    }

    private async Task ScanFolderAsync(string folder)
    {
        if (IsBusy) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsBusy = true;
        ClearError();
        StatusMessage = "Scanning music…";

        try
        {
            var progress = new Progress<string>(message =>
            {
                // Progress callbacks arrive asynchronously and can be queued behind the
                // completion continuation; never let one overwrite the final result text.
                if (IsBusy)
                {
                    StatusMessage = message;
                }
            });

            var sourcePath = folder;
            var allFiles = new List<string>();

            // Android's media index is the one place that reliably sees Music, Download,
            // Telegram, WhatsApp and other app folders under scoped storage. Its discovery
            // applies the music rules; this view model must not add raw folder audio back.
            if (_fileDiscovery.ScansWholeLibrary)
            {
                var discoveredFiles = await _fileDiscovery.DiscoverAsync(folder, ct);

                if (!_fileDiscovery.HasLibraryAccess)
                {
                    StatusMessage = "Music & audio access is off. Allow it in Android Settings, then tap Rescan.";
                    return;
                }

                if (discoveredFiles.Count > 0)
                {
                    sourcePath = _fileDiscovery.SourcePath;
                    allFiles.AddRange(discoveredFiles);
                }
            }

            var result = allFiles.Count > 0
                ? await _scanner.ScanFilesAsync(allFiles, progress, ct)
                : await _scanner.ScanAsync(folder, progress, ct);

            IsBusy = false;
            _library.ApplyScanResult(sourcePath, result);
            Preferences.Default.Set(LastFolderPreferenceKey, sourcePath);

            // Persist right away so the next launch restores instantly instead of rescanning.
            try
            {
                await _snapshotStore.SaveAsync(_library.CreateSnapshot());
            }
            catch (Exception saveEx) when (saveEx is IOException or UnauthorizedAccessException)
            {
                // A failed snapshot save only costs a rescan next launch.
            }

            if (result.Tracks.Count == 0)
            {
                StatusMessage = allFiles.Count == 0
                    ? result.Errors.Count == 0
                        ? $"No supported audio files found in \"{folder}\". Tap Scan to choose another folder."
                        : $"No tracks could be read ({result.Errors.Count} file(s) failed). Check storage permissions."
                    : result.Errors.Count == 0
                        ? "No supported audio files found on this device. Check media permissions and tap Rescan."
                        : $"No tracks could be read ({result.Errors.Count} file(s) failed). Check storage permissions.";
            }
            else
            {
                // The header already shows the song count, so only unreadable files need a
                // line of their own after a successful scan.
                StatusMessage = result.Errors.Count == 0
                    ? string.Empty
                    : $"{result.Errors.Count} file(s) could not be read.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            SetError($"Scan failed: {ex.Message}");
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SyncFromLibrary()
    {
        ApplyFilter();
        OnPropertyChanged(nameof(LibrarySummary));

        // Deleting a track changes the library size without going through a scan, which would
        // leave the status line claiming a stale count. Track edits keep the count intact, so
        // they don't disturb richer messages such as the unreadable-file count from a scan.
        var trackCount = _library.Tracks.Count;
        if (!IsBusy && _lastTrackCount >= 0 && trackCount != _lastTrackCount && HasStatus)
        {
            StatusMessage = trackCount == 0 ? "No tracks loaded." : string.Empty;
        }

        _lastTrackCount = trackCount;
    }

    /// <summary>
    /// Filtering rebuilds four collections, so rebuild once the user pauses typing instead of
    /// on every keystroke (long lists used to make the keyboard feel laggy).
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;

        _ = DebouncedFilterAsync(token);
    }

    private async Task DebouncedFilterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;

        var trackQuery = string.IsNullOrEmpty(query)
            ? _library.Tracks.AsEnumerable()
            : _library.Tracks.Where(t =>
                Contains(t.Name, query) ||
                Contains(t.ArtistName, query) ||
                Contains(t.Album?.Title, query));

        var albumQuery = string.IsNullOrEmpty(query)
            ? _library.Albums.AsEnumerable()
            : _library.Albums.Where(a =>
                Contains(a.Title, query) ||
                Contains(a.AlbumArtist, query));

        var artistQuery = string.IsNullOrEmpty(query)
            ? _library.Artists.AsEnumerable()
            : _library.Artists.Where(a => Contains(a.Name, query));

        var playlistQuery = string.IsNullOrEmpty(query)
            ? _library.Playlists.AsEnumerable()
            : _library.Playlists.Where(p => Contains(p.Name, query));

        Tracks = new ObservableCollection<Music>(SortTracks(trackQuery));
        var sortedAlbums = SortAlbums(albumQuery).ToList();
        Albums = new ObservableCollection<Album>(sortedAlbums);
        AlbumRows = new ObservableCollection<AlbumRow>(
            sortedAlbums
                .Select((album, index) => new { Album = album, Index = index })
                .GroupBy(x => x.Index / 2)
                .Select(g => new AlbumRow
                {
                    Left = g.First().Album,
                    Right = g.Skip(1).FirstOrDefault()?.Album
                }));
        Artists = new ObservableCollection<Artist>(SortArtists(artistQuery));
        var sortedPlaylists = SortPlaylists(playlistQuery).ToList();
        Playlists = new ObservableCollection<Playlist>(sortedPlaylists);
        PlaylistRows = new ObservableCollection<PlaylistRow>(
            sortedPlaylists
                .Select((playlist, index) => new { Playlist = playlist, Index = index })
                .GroupBy(x => x.Index / 2)
                .Select(g => new PlaylistRow
                {
                    Left = g.First().Playlist,
                    Right = g.Skip(1).FirstOrDefault()?.Playlist
                }));
        VisibleTrackCount = Tracks.Count;
        OnPropertyChanged(nameof(TabCountText));

        UpdateRecents(showShelves: string.IsNullOrEmpty(query));
    }

    /// <summary>
    /// Home shelves above the full track list. They are hidden while the user searches so the
    /// filtered rows stay the only thing on screen.
    /// </summary>
    private void UpdateRecents(bool showShelves)
    {
        List<Music> played = showShelves
            ? _library.Tracks
                .Where(track => track.LastPlayedAt.HasValue)
                .OrderByDescending(track => track.LastPlayedAt)
                .Take(10)
                .ToList()
            : [];

        List<Music> added = showShelves
            ? _library.Tracks
                .OrderByDescending(track => track.DateAdded)
                .Take(10)
                .ToList()
            : [];

        RecentlyPlayed = new ObservableCollection<Music>(played);
        RecentlyAdded = new ObservableCollection<Music>(added);
        ShowRecentlyPlayed = played.Count > 0;
        ShowRecentlyAdded = added.Count > 0;
    }

    private IEnumerable<Music> SortTracks(IEnumerable<Music> tracks) => _trackSort switch
    {
        "title_desc" => tracks.OrderByDescending(t => t.Name, StringComparer.OrdinalIgnoreCase),
        "artist" => tracks
            .OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Album?.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DiscNumber ?? 0)
            .ThenBy(t => t.TrackNumber ?? 0)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        "album" => tracks
            .OrderBy(t => t.Album?.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DiscNumber ?? 0)
            .ThenBy(t => t.TrackNumber ?? 0)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        "year" => tracks
            .OrderByDescending(t => t.Year ?? 0)
            .ThenBy(t => t.Album?.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.TrackNumber ?? 0),
        "duration" => tracks
            .OrderByDescending(t => t.Duration)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        "modified" => tracks
            .OrderByDescending(t => t.DateModified ?? t.DateAdded)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        "recent" => tracks
            .OrderByDescending(t => t.DateAdded)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        _ => tracks.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
    };

    private IEnumerable<Album> SortAlbums(IEnumerable<Album> albums) => _albumSort switch
    {
        "artist" => albums
            .OrderBy(a => a.AlbumArtist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
        "year" => albums
            .OrderByDescending(a => a.Year ?? 0)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
        _ => albums.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
    };

    private IEnumerable<Artist> SortArtists(IEnumerable<Artist> artists) => _artistSort switch
    {
        "songs" => artists
            .OrderByDescending(a => a.TrackCount)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
        _ => artists.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
    };

    private IEnumerable<Playlist> SortPlaylists(IEnumerable<Playlist> playlists) => _playlistSort switch
    {
        "updated" => playlists
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        "songs" => playlists
            .OrderByDescending(p => p.TrackCount)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        _ => playlists.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
    };

    private static bool Contains(string? value, string query)
        => !string.IsNullOrEmpty(value) &&
           value.Contains(query, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void PlayTrack(Music? track)
    {
        if (track is null) return;

        // Play from the currently visible list so next/previous stay within the filter.
        // Tapping the row only starts playback; the artwork tap opens Now Playing.
        _playerService.SetQueue(Tracks, Math.Max(0, Tracks.IndexOf(track)));
    }

    [RelayCommand]
    private async Task OpenTrackAsync(Music? track)
    {
        if (track is null) return;

        _playerService.SetQueue(Tracks, Math.Max(0, Tracks.IndexOf(track)));
        await Shell.Current.GoToAsync(Routes.NowPlaying);
    }

    [RelayCommand]
    private async Task PlayAlbumAsync(Album? album)
    {
        if (album?.Tracks.Count > 0)
        {
            _playerService.SetQueue(album.Tracks);
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private async Task PlayArtistAsync(Artist? artist)
    {
        if (artist?.Tracks.Count > 0)
        {
            _playerService.SetQueue(artist.Tracks);
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private async Task PlayPlaylistAsync(Playlist? playlist)
    {
        var tracks = playlist?.Tracks.Select(t => t.Music).OfType<Music>().ToList();
        if (tracks is { Count: > 0 })
        {
            _playerService.SetQueue(tracks);
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private async Task NavigateToAlbumDetailAsync(Album? album)
    {
        if (album is null) return;
        await Shell.Current.GoToAsync($"{Routes.AlbumDetail}?albumId={album.Id}");
    }

    [RelayCommand]
    private async Task NavigateToArtistDetailAsync(Artist? artist)
    {
        if (artist is null) return;
        await Shell.Current.GoToAsync($"{Routes.ArtistDetail}?artistId={artist.Id}");
    }

    [RelayCommand]
    private async Task NavigateToPlaylistDetailAsync(Playlist? playlist)
    {
        if (playlist is null) return;
        await Shell.Current.GoToAsync($"{Routes.PlaylistDetail}?playlistId={playlist.Id}");
    }

    [RelayCommand]
    private void SelectTab(string? index)
    {
        if (int.TryParse(index, out var i) && i is >= 0 and <= 3)
        {
            SelectedTabIndex = i;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Creates a playlist from the Lists tab's "+" button.</summary>
    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = await AppDialogs.PromptAsync(
            Shell.Current?.CurrentPage,
            "New playlist",
            "Create",
            "Name it after a mood, a night, a place…");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var playlist = _library.CreatePlaylist(name);
        StatusMessage = $"\"{playlist.Name}\" created. Add songs from any track's ⋮ menu.";
    }

    [RelayCommand]
    private async Task ShowSortAsync()
    {
        var (title, options, current) = SelectedTabIndex switch
        {
            0 => ("Sort tracks",
                new (string Key, string Label)[]
                {
                    ("title", "Title A–Z"),
                    ("title_desc", "Title Z–A"),
                    ("artist", "Artist"),
                    ("album", "Album"),
                    ("year", "Year"),
                    ("duration", "Duration"),
                    ("modified", "Date modified"),
                    ("recent", "Recently added")
                },
                _trackSort),
            1 => ("Sort albums",
                new (string Key, string Label)[]
                {
                    ("title", "Title A–Z"),
                    ("artist", "Album artist"),
                    ("year", "Year (newest first)")
                },
                _albumSort),
            2 => ("Sort artists",
                new (string Key, string Label)[]
                {
                    ("name", "Name A–Z"),
                    ("songs", "Most songs")
                },
                _artistSort),
            _ => ("Sort lists",
                new (string Key, string Label)[]
                {
                    ("name", "Name A–Z"),
                    ("updated", "Recently updated"),
                    ("songs", "Most songs")
                },
                _playlistSort)
        };

        var actions = options
            .Select(option => new AppSheetAction
            {
                Label = option.Label,
                Glyph = "\ue164",
                IsSelected = option.Key == current
            })
            .ToList();

        var index = await AppDialogs.ShowActionsAsync(
            Shell.Current?.CurrentPage,
            title,
            actions,
            "Choose how this list is ordered");

        if (index < 0 || index >= options.Length)
        {
            return;
        }

        var key = options[index].Key;

        switch (SelectedTabIndex)
        {
            case 0:
                _trackSort = key;
                Preferences.Default.Set(TrackSortKey, key);
                break;
            case 1:
                _albumSort = key;
                Preferences.Default.Set(AlbumSortKey, key);
                break;
            case 2:
                _artistSort = key;
                Preferences.Default.Set(ArtistSortKey, key);
                break;
            default:
                _playlistSort = key;
                Preferences.Default.Set(PlaylistSortKey, key);
                break;
        }

        ApplyFilter();
        OnPropertyChanged(nameof(SortDisplayName));
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTracksTab));
        OnPropertyChanged(nameof(IsAlbumsTab));
        OnPropertyChanged(nameof(IsArtistsTab));
        OnPropertyChanged(nameof(IsPlaylistsTab));
        OnPropertyChanged(nameof(SortDisplayName));
        OnPropertyChanged(nameof(TabCountText));
    }
}
