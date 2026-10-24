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
    private ObservableCollection<Artist> _artists = [];

    [ObservableProperty]
    private ObservableCollection<Playlist> _playlists = [];

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
        _ => "Name"
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
        SyncFromLibrary();
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

            StatusMessage = $"{snapshot.Tracks.Count} tracks loaded.";

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

                await MainThread.InvokeOnMainThreadAsync(
                    async () => await _snapshotStore.SaveAsync(_library.CreateSnapshot()));
            }
            catch
            {
                // A missing or unreadable cover is cosmetic; the 512 px fallback still works.
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
                : "Library is up to date. Tap Rescan to look for new files.";

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
            var sourceLabel = Path.GetFileName(folder);
            IReadOnlyList<string>? discoveredFiles = null;

            // Android's media index is the one place that reliably sees Download, Telegram,
            // WhatsApp and other app folders under scoped storage.
            if (_fileDiscovery.ScansWholeLibrary)
            {
                discoveredFiles = await _fileDiscovery.DiscoverAsync(folder, ct);
                if (discoveredFiles.Count > 0)
                {
                    sourcePath = _fileDiscovery.SourcePath;
                    sourceLabel = _fileDiscovery.SourceLabel;
                }
                else
                {
                    discoveredFiles = null;
                }
            }

            var result = discoveredFiles is { Count: > 0 }
                ? await _scanner.ScanFilesAsync(discoveredFiles, progress, ct)
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
                StatusMessage = discoveredFiles is null
                    ? result.Errors.Count == 0
                        ? $"No supported audio files found in \"{folder}\". Tap Scan to choose another folder."
                        : $"No tracks could be read ({result.Errors.Count} file(s) failed). Check storage permissions."
                    : result.Errors.Count == 0
                        ? "No supported audio files found on this device. Check media permissions and tap Rescan."
                        : $"No tracks could be read ({result.Errors.Count} file(s) failed). Check storage permissions.";
            }
            else
            {
                StatusMessage = result.Errors.Count == 0
                    ? $"{result.Tracks.Count} tracks loaded from {sourceLabel}."
                    : $"{result.Tracks.Count} tracks loaded from {sourceLabel}, {result.Errors.Count} file(s) could not be read.";
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
            StatusMessage = trackCount == 0 ? "No tracks loaded." : $"{trackCount} tracks loaded.";
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
        Albums = new ObservableCollection<Album>(SortAlbums(albumQuery));
        Artists = new ObservableCollection<Artist>(SortArtists(artistQuery));
        Playlists = new ObservableCollection<Playlist>(SortPlaylists(playlistQuery));
        VisibleTrackCount = Tracks.Count;
        OnPropertyChanged(nameof(TabCountText));
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
            _ => ("Sort playlists",
                new (string Key, string Label)[]
                {
                    ("name", "Name A–Z")
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
