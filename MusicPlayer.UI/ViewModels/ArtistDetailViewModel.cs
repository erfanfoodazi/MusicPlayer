using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class ArtistDetailViewModel : BaseViewModel, IPlayTrackContext
{
    System.Windows.Input.ICommand IPlayTrackContext.PlayTrackCommand => PlayTrackCommand;
    System.Windows.Input.ICommand IPlayTrackContext.OpenTrackCommand => OpenTrackCommand;

    private readonly PlayerService _playerService;
    private readonly LibraryService _library;
    private int _artistId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtist))]
    [NotifyPropertyChangedFor(nameof(ArtistInitial))]
    private Artist? _artist;

    [ObservableProperty]
    private ObservableCollection<Music> _topTracks = [];

    [ObservableProperty]
    private ObservableCollection<Album> _albums = [];

    public bool HasArtist => Artist is not null;

    public string ArtistInitial =>
        string.IsNullOrWhiteSpace(Artist?.Name) ? "?" : Artist!.Name[..1].ToUpperInvariant();

    public ArtistDetailViewModel(PlayerService playerService, LibraryService library)
    {
        _playerService = playerService;
        _library = library;
    }

    public void SetArtistId(int artistId) => _artistId = artistId;

    [RelayCommand]
    private Task LoadArtistAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        IsBusy = true;
        ClearError();

        try
        {
            Artist = _library.FindArtist(_artistId);

            if (Artist is null)
            {
                TopTracks = [];
                Albums = [];
                SetError("This artist is no longer in the library. Rescan your music folder.");
                return Task.CompletedTask;
            }

            TopTracks = new ObservableCollection<Music>(
                Artist.Tracks
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(10));

            Albums = new ObservableCollection<Album>(
                Artist.Tracks
                    .Select(t => t.Album)
                    .OfType<Album>()
                    .Distinct()
                    .OrderBy(a => a.Year ?? int.MaxValue)
                    .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase));

            Title = Artist.Name;
            OnPropertyChanged(nameof(ArtistInitial));
        }
        catch (Exception ex)
        {
            SetError($"Failed to load artist: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PlayArtistAsync()
    {
        if (Artist?.Tracks.Count > 0)
        {
            _playerService.SetQueue(Artist.Tracks);
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private async Task ShufflePlayAsync()
    {
        if (Artist?.Tracks.Count > 0)
        {
            _playerService.SetQueue(Artist.Tracks.OrderBy(_ => Guid.NewGuid()));
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private void PlayTrack(Music? track)
    {
        if (track is null || Artist is null) return;

        _playerService.SetQueue(Artist.Tracks, Math.Max(0, Artist.Tracks.IndexOf(track)));
    }

    [RelayCommand]
    private async Task OpenTrackAsync(Music? track)
    {
        if (track is null || Artist is null) return;

        _playerService.SetQueue(Artist.Tracks, Math.Max(0, Artist.Tracks.IndexOf(track)));
        await Shell.Current.GoToAsync(Routes.NowPlaying);
    }

    [RelayCommand]
    private async Task NavigateToAlbumAsync(Album? album)
    {
        if (album is null) return;
        await Shell.Current.GoToAsync($"{Routes.AlbumDetail}?albumId={album.Id}");
    }
}
