using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class SearchViewModel : BaseViewModel, IPlayTrackContext
{
    System.Windows.Input.ICommand IPlayTrackContext.PlayTrackCommand => PlayTrackCommand;
    System.Windows.Input.ICommand IPlayTrackContext.OpenTrackCommand => OpenTrackCommand;

    private readonly PlayerService _playerService;
    private readonly LibraryService _library;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Music> _trackResults = [];

    [ObservableProperty]
    private ObservableCollection<Album> _albumResults = [];

    [ObservableProperty]
    private ObservableCollection<Artist> _artistResults = [];

    [ObservableProperty]
    private ObservableCollection<Playlist> _playlistResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _hasSearched;

    public bool ShowEmptyHint =>
        !IsSearching && !HasSearched && TrackResults.Count == 0;

    public bool ShowNoResults =>
        !IsSearching && HasSearched &&
        TrackResults.Count == 0 && AlbumResults.Count == 0 &&
        ArtistResults.Count == 0 && PlaylistResults.Count == 0;

    public SearchViewModel(PlayerService playerService, LibraryService library)
    {
        _playerService = playerService;
        _library = library;
        Title = "Search";
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            ClearResults();
            HasSearched = false;
            return;
        }

        _ = DebouncedSearchAsync(value, token);
    }

    private async Task DebouncedSearchAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            await SearchCoreAsync(query, token);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this search.
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var term = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
        {
            ClearResults();
            HasSearched = false;
            return;
        }

        await SearchCoreAsync(term, CancellationToken.None);
    }

    private Task SearchCoreAsync(string query, CancellationToken token)
    {
        IsSearching = true;
        ClearError();

        try
        {
            token.ThrowIfCancellationRequested();

            var tracks = _library.Tracks
                .Where(t => Contains(t.Name, query) ||
                            Contains(t.ArtistName, query) ||
                            Contains(t.Album?.Title, query))
                .ToList();

            var albums = _library.Albums
                .Where(a => Contains(a.Title, query) || Contains(a.AlbumArtist, query))
                .ToList();

            var artists = _library.Artists
                .Where(a => Contains(a.Name, query))
                .ToList();

            var playlists = _library.Playlists
                .Where(p => Contains(p.Name, query))
                .ToList();

            if (!token.IsCancellationRequested)
            {
                TrackResults = new ObservableCollection<Music>(tracks);
                AlbumResults = new ObservableCollection<Album>(albums);
                ArtistResults = new ObservableCollection<Artist>(artists);
                PlaylistResults = new ObservableCollection<Playlist>(playlists);
                HasSearched = true;
                NotifyResultState();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore superseded search.
        }
        catch (Exception ex)
        {
            SetError($"Search failed: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }

        return Task.CompletedTask;
    }

    private void NotifyResultState()
    {
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private static bool Contains(string? value, string query)
        => !string.IsNullOrEmpty(value) &&
           value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void ClearResults()
    {
        TrackResults = [];
        AlbumResults = [];
        ArtistResults = [];
        PlaylistResults = [];
        NotifyResultState();
    }

    [RelayCommand]
    private void PlayTrack(Music? track)
    {
        if (track is null) return;

        _playerService.SetQueue(TrackResults, Math.Max(0, TrackResults.IndexOf(track)));
    }

    [RelayCommand]
    private async Task OpenTrackAsync(Music? track)
    {
        if (track is null) return;

        _playerService.SetQueue(TrackResults, Math.Max(0, TrackResults.IndexOf(track)));
        await Shell.Current.GoToAsync(Routes.NowPlaying);
    }
}
