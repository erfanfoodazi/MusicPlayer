using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// Lets the user tick several songs and append them to one playlist in a single action.
/// Songs already in the playlist are hidden, so nothing here can create a duplicate entry.
/// </summary>
public partial class AddSongsViewModel : BaseViewModel
{
    private readonly LibraryService _library;
    private readonly List<SelectableTrack> _all = [];
    private int _playlistId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private Playlist? _playlist;

    [ObservableProperty]
    private ObservableCollection<SelectableTrack> _tracks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddButtonText))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _hasTracks;

    [ObservableProperty]
    private string _emptyMessage = "No songs to add.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public AddSongsViewModel(LibraryService library)
    {
        _library = library;
        Title = "Add songs";
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasSelection => SelectedCount > 0;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanAdd => HasTracks && SelectedCount > 0 && !IsBusy;

    public string AddButtonText => SelectedCount switch
    {
        0 => "Add songs",
        1 => "Add 1 song",
        _ => $"Add {SelectedCount} songs"
    };

    public string Subtitle => Playlist is null
        ? string.Empty
        : Playlist.TrackCount == 1
            ? "1 song in this list"
            : $"{Playlist.TrackCount} songs in this list";

    public void SetPlaylistId(int playlistId) => _playlistId = playlistId;

    [RelayCommand]
    private void Load()
    {
        Playlist = _library.FindPlaylist(_playlistId);

        if (Playlist is null)
        {
            SetError("This playlist is no longer available.");
            Tracks = [];
            HasTracks = false;
            EmptyMessage = "Go back and open the playlist again.";
            return;
        }

        Title = $"Add to {Playlist.Name}";

        foreach (var item in _all)
        {
            item.PropertyChanged -= OnTrackSelectionChanged;
        }

        _all.Clear();

        var existing = new HashSet<string>(
            Playlist.Tracks
                .Select(entry => entry.Music?.FilePath ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var track in _library.Tracks.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(track.FilePath) || existing.Contains(track.FilePath))
            {
                continue;
            }

            var item = new SelectableTrack(track);
            item.PropertyChanged += OnTrackSelectionChanged;
            _all.Add(item);
        }

        OnPropertyChanged(nameof(Subtitle));
        ApplyFilter();
    }

    private void OnTrackSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTrack.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        SelectedCount = _all.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;

        var matches = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(item =>
                Contains(item.Track.Name, query) ||
                Contains(item.Track.ArtistName, query) ||
                Contains(item.Track.Album?.Title, query)).ToList();

        Tracks = new ObservableCollection<SelectableTrack>(matches);
        HasTracks = _all.Count > 0;

        EmptyMessage = _library.Tracks.Count == 0
            ? "Your library is empty. Scan a music folder first."
            : _all.Count == 0
                ? "Every song in your library is already in this list."
                : "No songs match that search.";

        UpdateSelectionState();
    }

    private static bool Contains(string? value, string query)
        => !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ToggleTrack(SelectableTrack? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void SelectAll()
    {
        var select = Tracks.Any(item => !item.IsSelected);
        foreach (var item in Tracks)
        {
            item.IsSelected = select;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in _all)
        {
            item.IsSelected = false;
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Playlist is null || IsBusy)
        {
            return;
        }

        var selected = _all.Where(item => item.IsSelected).Select(item => item.Track).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var added = _library.AddTracksToPlaylist(Playlist, selected);
            Haptics.Tap();

            if (added == 0)
            {
                StatusMessage = "Those songs are already in this playlist.";
                return;
            }

            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add songs: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
