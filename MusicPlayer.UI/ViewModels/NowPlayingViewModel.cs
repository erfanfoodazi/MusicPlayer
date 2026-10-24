using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class NowPlayingViewModel : BaseViewModel
{
    private readonly PlayerService _playerService;
    private bool _syncingVolume;
    private ImageSource? _artworkSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentTrack))]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDisplayName))]
    [NotifyPropertyChangedFor(nameof(CurrentTrackArtist))]
    [NotifyPropertyChangedFor(nameof(CurrentAlbumTitle))]
    [NotifyPropertyChangedFor(nameof(HasAlbum))]
    [NotifyPropertyChangedFor(nameof(ArtworkSource))]
    private Music? _currentTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    private PlaybackState _state;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderMaximum))]
    private double _duration;

    public double SliderMaximum => Math.Max(Duration, 1);

    [ObservableProperty]
    private double _volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleGlyph))]
    private bool _isShuffle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatGlyph))]
    [NotifyPropertyChangedFor(nameof(RepeatIcon))]
    [NotifyPropertyChangedFor(nameof(IsRepeatActive))]
    private RepeatMode _repeatMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaybackError))]
    private string? _playbackError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueCountText))]
    [NotifyPropertyChangedFor(nameof(HasQueueItems))]
    private ObservableCollection<QueueSection> _queueSections = [];

    public bool HasQueueItems => QueueSections.Count > 0;

    public bool HasCurrentTrack => CurrentTrack is not null;

    public string CurrentTrackDisplayName => CurrentTrack?.Name ?? "Nothing playing";

    public string CurrentTrackArtist => CurrentTrack?.ArtistName ?? "Choose a song from your library";

    public string CurrentAlbumTitle => CurrentTrack?.Album?.Title ?? string.Empty;

    public bool HasAlbum => !string.IsNullOrWhiteSpace(CurrentAlbumTitle);

    public ImageSource ArtworkSource =>
        _artworkSource ??= !string.IsNullOrWhiteSpace(CurrentTrack?.ArtworkPath)
            ? ImageSource.FromFile(CurrentTrack!.ArtworkPath!)
            : ImageSource.FromFile("placeholder_album.png");

    public string PlayPauseGlyph => State == PlaybackState.Playing ? "❚❚" : "▶";

    public string PlayPauseIcon => State == PlaybackState.Playing ? "\ue034" : "\ue037";

    public bool IsPlaying => State == PlaybackState.Playing;

    public string RepeatIcon => RepeatMode == RepeatMode.One ? "\ue041" : "\ue040";

    public bool IsRepeatActive => RepeatMode != RepeatMode.Off;

    public string ShuffleIcon => "\ue043";

    public string RepeatGlyph => RepeatMode switch
    {
        RepeatMode.All => "Repeat: All",
        RepeatMode.One => "Repeat: One",
        _ => "Repeat: Off"
    };

    public string ShuffleGlyph => IsShuffle ? "Shuffle: On" : "Shuffle: Off";

    // Material Icons glyphs: the button renders with the icon font.
    public string MuteGlyph => IsMuted ? "\ue04f" : "\ue050";

    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(PlaybackError);

    public string QueueCountText
    {
        get
        {
            var played = QueueSections.FirstOrDefault(section => section.IsPlayed)?.Count ?? 0;
            var upNext = QueueSections.FirstOrDefault(section => !section.IsPlayed)?.Count ?? 0;

            if (played == 0 && upNext == 0)
            {
                return "Nothing in the queue";
            }

            if (played == 0)
            {
                return upNext == 1 ? "1 song up next" : $"{upNext} songs up next";
            }

            if (upNext == 0)
            {
                return played == 1 ? "1 played song" : $"{played} played songs";
            }

            return $"{played} played · {upNext} up next";
        }
    }

    public string SpeedLabel => _playerService.SpeedDisplay;

    public string PitchLabel => _playerService.PitchDisplay;

    public string SleepLabel => _playerService.SleepDisplay;

    public string EqualizerLabel => _playerService.EqualizerDisplay;

    public bool SupportsPitch => _playerService.SupportsPitch;

    public bool SupportsEqualizer => _playerService.SupportsEqualizer;

    public NowPlayingViewModel(PlayerService playerService)
    {
        _playerService = playerService;
        Title = "Now Playing";

        _playerService.PropertyChanged += OnPlayerServicePropertyChanged;
        _playerService.QueueChanged += OnQueueChanged;
        SyncFromPlayerService();
    }

    /// <summary>Called when the page appears; re-syncs in case the singleton changed elsewhere.</summary>
    public void Refresh() => SyncFromPlayerService();

    partial void OnCurrentTrackChanged(Music? value) => _artworkSource = null;

    private void OnQueueChanged()
    {
        if (MainThread.IsMainThread)
        {
            UpdateQueue();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(UpdateQueue);
        }
    }

    private void OnPlayerServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            ApplyPlayerProperty(e.PropertyName);
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => ApplyPlayerProperty(e.PropertyName));
        }
    }

    private void ApplyPlayerProperty(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(PlayerService.CurrentTrack):
                CurrentTrack = _playerService.CurrentTrack;
                UpdateQueue();
                break;
            case nameof(PlayerService.State):
                State = _playerService.State;
                break;
            case nameof(PlayerService.Position):
                Position = _playerService.Position;
                break;
            case nameof(PlayerService.Duration):
                Duration = _playerService.Duration;
                break;
            case nameof(PlayerService.Volume):
                _syncingVolume = true;
                Volume = _playerService.Volume;
                _syncingVolume = false;
                break;
            case nameof(PlayerService.IsMuted):
                IsMuted = _playerService.IsMuted;
                break;
            case nameof(PlayerService.IsShuffle):
                IsShuffle = _playerService.IsShuffle;
                break;
            case nameof(PlayerService.RepeatMode):
                RepeatMode = _playerService.RepeatMode;
                break;
            case nameof(PlayerService.PlaybackError):
                PlaybackError = _playerService.PlaybackError;
                break;
            case nameof(PlayerService.PlaybackSpeed):
            case nameof(PlayerService.Pitch):
            case nameof(PlayerService.IsEqualizerEnabled):
            case nameof(PlayerService.EqualizerPreset):
            case nameof(PlayerService.SleepTimerDisplay):
                NotifyAudioToolLabels();
                break;
        }
    }

    private void NotifyAudioToolLabels()
    {
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(PitchLabel));
        OnPropertyChanged(nameof(SleepLabel));
        OnPropertyChanged(nameof(EqualizerLabel));
        OnPropertyChanged(nameof(SupportsPitch));
        OnPropertyChanged(nameof(SupportsEqualizer));
    }

    private void SyncFromPlayerService()
    {
        CurrentTrack = _playerService.CurrentTrack;
        State = _playerService.State;
        Position = _playerService.Position;
        Duration = _playerService.Duration;
        Volume = _playerService.Volume;
        IsMuted = _playerService.IsMuted;
        IsShuffle = _playerService.IsShuffle;
        RepeatMode = _playerService.RepeatMode;
        PlaybackError = _playerService.PlaybackError;
        NotifyAudioToolLabels();
        UpdateQueue();
    }

    private void UpdateQueue()
    {
        // The native queue is cyclic and keeps played songs at its tail. Presenting it as a
        // single "up next" list hid the tracks the user just heard. The sheet now has two
        // groups: played songs (most recent first) and the real upcoming queue.
        var current = CurrentTrack;
        var played = _playerService.PlayHistory
            .Where(track => !ReferenceEquals(track, current))
            .ToList();

        var playedLookup = new HashSet<Music>(played, ReferenceEqualityComparer.Instance);
        var upNext = _playerService.PlayQueue
            .Where(track => !ReferenceEquals(track, current) && !playedLookup.Contains(track))
            .ToList();

        var sections = new ObservableCollection<QueueSection>();
        if (played.Count > 0)
        {
            sections.Add(new QueueSection(
                "PREVIOUSLY PLAYED",
                played.Count == 1 ? "1 song" : $"{played.Count} songs",
                isPlayed: true,
                played));
        }

        if (upNext.Count > 0)
        {
            sections.Add(new QueueSection(
                "UP NEXT",
                upNext.Count == 1 ? "1 song" : $"{upNext.Count} songs",
                isPlayed: false,
                upNext));
        }

        QueueSections = sections;
        OnPropertyChanged(nameof(QueueCountText));
    }

    [RelayCommand]
    private void TogglePlayPause() => _playerService.TogglePlayPause();

    [RelayCommand]
    private void PlayNext() => _playerService.PlayNext();

    [RelayCommand]
    private void PlayPrevious() => _playerService.PlayPrevious();

    [RelayCommand]
    private void Seek(double position) => _playerService.Seek(position);

    [RelayCommand]
    private void ToggleMute() => _playerService.ToggleMute();

    [RelayCommand]
    private void ToggleShuffle() => _playerService.ToggleShuffle();

    [RelayCommand]
    private void CycleRepeatMode() => _playerService.CycleRepeatMode();

    [RelayCommand]
    private void RemoveFromQueue(Music? track)
    {
        if (track is null) return;
        _playerService.RemoveFromQueue(track);
    }

    [RelayCommand]
    private void ClearQueue() => _playerService.ClearQueue();

    [RelayCommand]
    private void PlayQueueItem(Music? track)
    {
        if (track is null) return;
        _playerService.PlayQueueTrack(track);
    }

    partial void OnVolumeChanged(double value)
    {
        if (!_syncingVolume)
        {
            _playerService.SetVolume(value);
        }
    }
}
