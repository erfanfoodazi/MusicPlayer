using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class AlbumDetailViewModel : BaseViewModel, IPlayTrackContext
{
    System.Windows.Input.ICommand IPlayTrackContext.PlayTrackCommand => PlayTrackCommand;
    System.Windows.Input.ICommand IPlayTrackContext.OpenTrackCommand => OpenTrackCommand;

    private readonly PlayerService _playerService;
    private readonly LibraryService _library;
    private int _albumId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbum))]
    [NotifyPropertyChangedFor(nameof(AlbumArtworkPath))]
    [NotifyPropertyChangedFor(nameof(AlbumYearText))]
    [NotifyPropertyChangedFor(nameof(AlbumMetaText))]
    private Album? _album;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumMetaText))]
    private ObservableCollection<Music> _tracks = [];

    public bool HasAlbum => Album is not null;

    public string? AlbumArtworkPath => Album?.ArtworkPath;

    public string AlbumYearText => Album?.Year is > 0 ? Album.Year.Value.ToString() : string.Empty;

    public string AlbumMetaText
    {
        get
        {
            var parts = new List<string>(3);

            if (Album?.Year is > 0)
            {
                parts.Add(Album.Year.Value.ToString());
            }

            if (Tracks.Count > 0)
            {
                parts.Add(Tracks.Count == 1 ? "1 track" : $"{Tracks.Count} tracks");

                var total = TimeSpan.Zero;
                foreach (var track in Tracks)
                {
                    total += track.Duration;
                }

                if (total > TimeSpan.Zero)
                {
                    parts.Add(total.TotalHours >= 1
                        ? $"{(int)total.TotalHours} hr {total.Minutes} min"
                        : $"{Math.Max(1, (int)Math.Round(total.TotalMinutes))} min");
                }
            }

            return string.Join(" · ", parts);
        }
    }

    public AlbumDetailViewModel(PlayerService playerService, LibraryService library)
    {
        _playerService = playerService;
        _library = library;
    }

    public void SetAlbumId(int albumId) => _albumId = albumId;

    [RelayCommand]
    private Task LoadAlbumAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        IsBusy = true;
        ClearError();

        try
        {
            Album = _library.FindAlbum(_albumId);

            if (Album is null)
            {
                Tracks = [];
                SetError("This album is no longer in the library. Rescan your music folder.");
                return Task.CompletedTask;
            }

            var ordered = Album.Tracks
                .OrderBy(t => t.DiscNumber ?? 0)
                .ThenBy(t => t.TrackNumber ?? int.MaxValue)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

            Tracks = new ObservableCollection<Music>(ordered);
            Title = Album.Title;
            OnPropertyChanged(nameof(AlbumArtworkPath));
            OnPropertyChanged(nameof(AlbumYearText));
            OnPropertyChanged(nameof(AlbumMetaText));
        }
        catch (Exception ex)
        {
            SetError($"Failed to load album: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PlayAlbumAsync()
    {
        if (Tracks.Count > 0)
        {
            _playerService.SetQueue(Tracks);
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
    private async Task ShufflePlayAsync()
    {
        if (Tracks.Count == 0) return;

        var shuffled = Tracks.OrderBy(_ => Guid.NewGuid()).ToList();
        _playerService.SetQueue(shuffled);
        await Shell.Current.GoToAsync(Routes.NowPlaying);
    }
}
