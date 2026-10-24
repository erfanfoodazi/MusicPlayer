using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class PlaylistDetailViewModel : BaseViewModel, IPlayTrackContext
{
    System.Windows.Input.ICommand IPlayTrackContext.PlayTrackCommand => PlayTrackCommand;
    System.Windows.Input.ICommand IPlayTrackContext.OpenTrackCommand => OpenTrackCommand;

    private readonly PlayerService _playerService;
    private readonly LibraryService _library;
    private int _playlistId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylist))]
    [NotifyPropertyChangedFor(nameof(PlaylistArtworkPath))]
    private Playlist? _playlist;

    [ObservableProperty]
    private ObservableCollection<Music> _tracks = [];

    public bool HasPlaylist => Playlist is not null;

    public string? PlaylistArtworkPath => Playlist?.ArtworkPath;

    public PlaylistDetailViewModel(PlayerService playerService, LibraryService library)
    {
        _playerService = playerService;
        _library = library;
    }

    public void SetPlaylistId(int playlistId) => _playlistId = playlistId;

    [RelayCommand]
    private Task LoadPlaylistAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        IsBusy = true;
        ClearError();

        try
        {
            Playlist = _library.FindPlaylist(_playlistId);

            if (Playlist is null)
            {
                Tracks = [];
                SetError("This playlist is no longer available.");
                return Task.CompletedTask;
            }

            Tracks = new ObservableCollection<Music>(
                Playlist.Tracks
                    .OrderBy(t => t.Order)
                    .Select(t => t.Music)
                    .OfType<Music>());

            Title = Playlist.Name;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load playlist: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PlayPlaylistAsync()
    {
        if (Tracks.Count > 0)
        {
            _playerService.SetQueue(Tracks);
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private async Task ShufflePlayAsync()
    {
        if (Tracks.Count > 0)
        {
            _playerService.SetQueue(Tracks.OrderBy(_ => Guid.NewGuid()));
            await Shell.Current.GoToAsync(Routes.NowPlaying);
        }
    }

    [RelayCommand]
    private void PlayTrack(Music? track)
    {
        if (track is null) return;

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
    private void RemoveTrack(Music? track)
    {
        if (track is null || Playlist is null) return;

        // Go through the library so the removal is persisted and every other screen sees
        // the new track count immediately.
        if (_library.RemoveTrackFromPlaylist(Playlist, track))
        {
            Tracks.Remove(track);
            OnPropertyChanged(nameof(Playlist));
        }
    }

    [RelayCommand]
    private async Task AddSongsAsync()
    {
        if (Playlist is null)
        {
            return;
        }

        await Shell.Current.GoToAsync($"{Routes.AddSongs}?playlistId={Playlist.Id}");
    }
}
