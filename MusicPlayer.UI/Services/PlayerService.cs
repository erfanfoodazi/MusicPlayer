using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services.Playback;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Playback state shared by the mini player, the full player, and every page.
/// Queue/history/repeat decisions live here; actual decoding is delegated to the
/// platform engine behind <see cref="IAudioPlayer"/>.
/// </summary>
public partial class PlayerService : ObservableObject
{
    public static PlayerService Instance { get; } = new();

    private const string PlaybackSpeedKey = "settings.playbackSpeed";
    private const string PitchKey = "settings.pitch";
    private const string EqualizerEnabledKey = "settings.equalizer";
    private const string EqualizerPresetKey = "settings.equalizerPreset";

    private readonly IAudioPlayer _audioPlayer;
    private readonly Queue<Music> _playQueue = [];
    private readonly Stack<Music> _history = [];
    private Music? _currentTrack;
    private IDispatcherTimer? _positionTimer;
    private CancellationTokenSource? _loadCancellation;
    private System.Threading.Timer? _sleepTimer;
    private DateTimeOffset? _sleepEndsAt;
    private bool _sleepAtEndOfTrack;
    private int _queueCycleLength;
    private int _tracksPlayedInCycle;

    // After a manual seek the decoder keeps reporting the old position for a moment. Ignoring
    // the engine clock briefly keeps the seek lines from snapping back to where they were.
    private DateTime _positionTrustedFrom = DateTime.MinValue;

    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Stopped;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private double _duration;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isShuffle;

    [ObservableProperty]
    private RepeatMode _repeatMode = RepeatMode.Off;

    [ObservableProperty]
    private string? _playbackError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
    private double _playbackSpeed = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchDisplay))]
    [NotifyPropertyChangedFor(nameof(PitchSemitones))]
    private double _pitch = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EqualizerDisplay))]
    private bool _isEqualizerEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EqualizerDisplay))]
    private string _equalizerPreset = "Normal";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepDisplay))]
    private string _sleepTimerDisplay = "Off";

    public Music? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (SetProperty(ref _currentTrack, value))
            {
                OnPropertyChanged(nameof(HasCurrentTrack));
                OnPropertyChanged(nameof(CurrentTrackDisplayName));
                OnPropertyChanged(nameof(CurrentTrackArtist));
                OnPropertyChanged(nameof(CurrentTrackArtwork));
                OnPropertyChanged(nameof(CurrentTrackListArtwork));
                OnPropertyChanged(nameof(CurrentAlbumTitle));
#if ANDROID
                MediaPlaybackBridge.Refresh();
#endif
            }
        }
    }

    public bool HasCurrentTrack => CurrentTrack != null;

    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(PlaybackError);

    public string CurrentTrackDisplayName => CurrentTrack?.Name ?? "No track";

    public string CurrentTrackArtist => CurrentTrack?.ArtistName ?? "Unknown Artist";

    public string CurrentAlbumTitle => CurrentTrack?.Album?.Title ?? string.Empty;

    /// <summary>Absolute path of the current cover, converted to an ImageSource by the UI.</summary>
    public string? CurrentTrackArtwork => CurrentTrack?.ArtworkPath ?? CurrentTrack?.Album?.ArtworkPath;

    /// <summary>Small cover for the mini player; avoids decoding a 512 px image at 46 dp.</summary>
    public string? CurrentTrackListArtwork => CurrentTrack?.ListArtworkPath ?? CurrentTrack?.Album?.ArtworkPath;

    public IReadOnlyList<Music> PlayQueue => _playQueue.ToList();

    /// <summary>Already-played tracks, most recent first. Used by the queue sheet and Previous.</summary>
    public IReadOnlyList<Music> PlayHistory => _history.ToList();

    public int QueueCount => _playQueue.Count;

    // Icon-font glyphs used by the redesigned transport controls.
    public string PlayPauseGlyph => State == PlaybackState.Playing ? "❚❚" : "▶";
    public string PlayPauseIcon => State == PlaybackState.Playing ? "\ue034" : "\ue037";
    public string RepeatGlyph => RepeatMode switch
    {
        RepeatMode.All => "↻ All",
        RepeatMode.One => "↻ One",
        _ => "↻ Off"
    };
    public string ShuffleGlyph => IsShuffle ? "Shuffle: On" : "Shuffle";
    public string MuteGlyph => IsMuted ? "\ue04f" : "\ue050";
    public string MuteIcon => IsMuted ? "\ue04f" : "\ue050";

    public bool SupportsPlaybackRate => _audioPlayer.SupportsPlaybackRate;
    public bool SupportsPitch => _audioPlayer.SupportsPitch;
    public bool SupportsEqualizer => _audioPlayer.SupportsEqualizer;
    public IReadOnlyList<string> EqualizerPresets => _audioPlayer.EqualizerPresets;

    public string SpeedDisplay => $"{PlaybackSpeed:0.##}×";

    public int PitchSemitones => (int)Math.Round(12 * Math.Log2(Math.Max(0.01, Pitch)));

    public string PitchDisplay => PitchSemitones == 0 ? "Pitch" : $"Pitch {PitchSemitones:+0;-0;0}";

    public string SleepDisplay => SleepTimerDisplay == "Off" ? "Sleep" : SleepTimerDisplay;

    public string EqualizerDisplay => IsEqualizerEnabled ? EqualizerPreset : "EQ";

    public event Action<Music>? TrackChanged;
    public event Action<PlaybackState>? StateChanged;
    public event Action<double>? PositionChanged;
    public event Action? QueueChanged;

    public IRelayCommand TogglePlayPauseCommand { get; }
    public IRelayCommand PlayNextCommand { get; }
    public IRelayCommand PlayPreviousCommand { get; }
    public IRelayCommand<double> SeekCommand { get; }
    public IRelayCommand<double> SetVolumeCommand { get; }
    public IRelayCommand ToggleMuteCommand { get; }
    public IRelayCommand ToggleShuffleCommand { get; }
    public IRelayCommand CycleRepeatModeCommand { get; }
    public IRelayCommand<Music> RemoveFromQueueCommand { get; }
    public IRelayCommand ClearQueueCommand { get; }

    private PlayerService()
    {
#if WINDOWS
        _audioPlayer = new WindowsAudioPlayer();
#elif ANDROID
        _audioPlayer = new AndroidAudioPlayer();
#else
        _audioPlayer = new NullAudioPlayer();
#endif

        _audioPlayer.PlaybackEnded += OnAudioPlayerPlaybackEnded;
        _audioPlayer.Failed += OnAudioPlayerFailed;
        _audioPlayer.AudioDeviceDisconnected += OnAudioDeviceDisconnected;
        _audioPlayer.SetVolume(Volume);

        TogglePlayPauseCommand = new RelayCommand(TogglePlayPause);
        PlayNextCommand = new RelayCommand(PlayNext);
        PlayPreviousCommand = new RelayCommand(PlayPrevious);
        SeekCommand = new RelayCommand<double>(Seek);
        SetVolumeCommand = new RelayCommand<double>(SetVolume);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        ToggleShuffleCommand = new RelayCommand(ToggleShuffle);
        CycleRepeatModeCommand = new RelayCommand(CycleRepeatMode);
        RemoveFromQueueCommand = new RelayCommand<Music>(RemoveFromQueue);
        ClearQueueCommand = new RelayCommand(ClearQueue);

        PlaybackSpeed = Preferences.Default.Get(PlaybackSpeedKey, 1.0);
        Pitch = Preferences.Default.Get(PitchKey, 1.0);
        IsEqualizerEnabled = Preferences.Default.Get(EqualizerEnabledKey, false);
        EqualizerPreset = Preferences.Default.Get(EqualizerPresetKey, "Normal");

        _audioPlayer.SetPlaybackRate(PlaybackSpeed);
        _audioPlayer.SetPitch(Pitch);
        _audioPlayer.SetEqualizerEnabled(IsEqualizerEnabled);
        if (IsEqualizerEnabled)
        {
            _audioPlayer.SetEqualizerPreset(EqualizerPreset);
        }
    }

    public void SetQueue(IEnumerable<Music> tracks, int startIndex = 0)
    {
        var items = tracks.Where(t => t is not null).ToList();
        _playQueue.Clear();
        _history.Clear();

        foreach (var track in items)
        {
            _playQueue.Enqueue(track);
        }

        if (_playQueue.Count > 0 && startIndex > 0 && startIndex < _playQueue.Count)
        {
            var skip = _playQueue.Take(startIndex).ToList();
            foreach (var item in skip)
            {
                _playQueue.Dequeue();
            }
            foreach (var item in skip)
            {
                _playQueue.Enqueue(item);
            }
        }

        _queueCycleLength = _playQueue.Count;
        _tracksPlayedInCycle = 0;
        OnQueueChanged();

        if (_playQueue.Count > 0)
        {
            // Starting a new album/playlist replaces the listening session; the old song
            // should not appear as "played" in the new queue.
            PlayNext(addToHistory: false);
        }
    }

    public void AddToQueue(Music track)
    {
        _playQueue.Enqueue(track);
        ExtendQueueCycleForAddedTracks(1);
        OnQueueChanged();
    }

    public void AddRangeToQueue(IEnumerable<Music> tracks)
    {
        var added = 0;
        foreach (var track in tracks)
        {
            _playQueue.Enqueue(track);
            added++;
        }

        ExtendQueueCycleForAddedTracks(added);
        OnQueueChanged();
    }

    public void ClearQueue()
    {
        _playQueue.Clear();
        _queueCycleLength = 0;
        _tracksPlayedInCycle = 0;
        OnQueueChanged();
    }

    public void RemoveFromQueue(Music? track)
    {
        if (track is null) return;

        var list = _playQueue.ToList();
        var removedFromUpNext = list.Remove(track);
        if (removedFromUpNext)
        {
            _playQueue.Clear();
            foreach (var item in list)
            {
                _playQueue.Enqueue(item);
            }

            _queueCycleLength = Math.Max(_tracksPlayedInCycle, _queueCycleLength - 1);
        }

        // The queue sheet also lists played tracks; their remove button should really
        // remove them instead of doing nothing.
        if (RemoveFromHistory(track))
        {
            // Nothing else to update: the stack was rebuilt in place.
        }

        OnQueueChanged();
    }

    private bool RemoveFromHistory(Music track)
    {
        var history = _history.ToList();
        var changed = false;
        while (history.Remove(track))
        {
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        _history.Clear();
        for (var index = history.Count - 1; index >= 0; index--)
        {
            _history.Push(history[index]);
        }

        return true;
    }

    /// <summary>
    /// Points queue, history and playback at the refreshed instance after a tag edit replaced
    /// a track in the library, so the player immediately shows the new tags/cover.
    /// </summary>
    public void HandleTrackReplaced(Music oldTrack, Music newTrack)
    {
        if (oldTrack is null || newTrack is null)
        {
            return;
        }

        var queue = _playQueue.Select(t => ReferenceEquals(t, oldTrack) ? newTrack : t).ToList();
        _playQueue.Clear();
        foreach (var item in queue)
        {
            _playQueue.Enqueue(item);
        }

        if (_history.Count > 0)
        {
            var history = _history
                .Select(t => ReferenceEquals(t, oldTrack) ? newTrack : t)
                .Reverse()
                .ToList();
            _history.Clear();
            foreach (var item in history)
            {
                _history.Push(item);
            }
        }

        if (ReferenceEquals(CurrentTrack, oldTrack))
        {
            CurrentTrack = newTrack;
        }

        OnQueueChanged();
    }

    /// <summary>
    /// Reacts to a track whose file was just deleted: it is removed from the queue and the
    /// history, and playback stops when the deleted file is the one currently playing.
    /// </summary>
    public void HandleTrackDeleted(Music track)
    {
        if (track is null) return;

        var isCurrent = ReferenceEquals(CurrentTrack, track);

        var queueBefore = _playQueue.Count;
        var queue = _playQueue.Where(t => !ReferenceEquals(t, track)).ToList();
        if (queue.Count != queueBefore)
        {
            _playQueue.Clear();
            foreach (var item in queue)
            {
                _playQueue.Enqueue(item);
            }

            _queueCycleLength = Math.Max(_tracksPlayedInCycle, _queueCycleLength - (queueBefore - queue.Count));
        }

        if (_history.Count > 0)
        {
            var history = _history.Where(t => !ReferenceEquals(t, track)).Reverse().ToList();
            _history.Clear();
            foreach (var item in history)
            {
                _history.Push(item);
            }
        }

        if (isCurrent)
        {
            Stop();
            CurrentTrack = null;
            Duration = 0;
            Position = 0;
        }

        OnQueueChanged();
    }

    /// <summary>Plays a track chosen directly by the user and replaces the current queue.</summary>
    public void Play(Music track)
    {
        if (track is null) return;

        _playQueue.Clear();
        _history.Clear();
        _queueCycleLength = 1;
        _tracksPlayedInCycle = 1;
        OnQueueChanged();

        _ = LoadTrackAsync(track, autoPlay: true, addToHistory: false);
    }

    /// <summary>Plays an item selected from the queue without discarding the queue.</summary>
    public void PlayQueueTrack(Music track)
    {
        if (track is null) return;

        // The tapped song is no longer "up next": remove this instance from the queue so the
        // cycle does not replay it after the rest of the queue has finished. A track chosen
        // from the played section is also removed from the history it came from.
        var queued = _playQueue.ToList();
        var removed = queued.Remove(track);
        if (removed)
        {
            _playQueue.Clear();
            foreach (var item in queued)
            {
                _playQueue.Enqueue(item);
            }

            _queueCycleLength = Math.Max(_tracksPlayedInCycle, _queueCycleLength - 1);
        }

        var removedFromHistory = RemoveFromHistory(track);

        if (removed || removedFromHistory)
        {
            OnQueueChanged();
        }

        _ = LoadTrackAsync(track, autoPlay: true, addToHistory: true);
    }

    public void PlayNext() => PlayNext(addToHistory: true);

    private void PlayNext(bool addToHistory)
    {
        if (_playQueue.Count == 0)
        {
            // There is no upcoming item. Restart the current song so the Next button
            // remains useful for a single-track library result.
            if (CurrentTrack is not null)
            {
                _ = LoadTrackAsync(CurrentTrack, autoPlay: true, addToHistory: false);
            }

            return;
        }

        var next = _playQueue.Dequeue();
        _playQueue.Enqueue(next);

        if (_queueCycleLength > 0)
        {
            _tracksPlayedInCycle++;
        }

        _ = LoadTrackAsync(next, autoPlay: true, addToHistory);
        OnQueueChanged();
    }

    public void PlayPrevious()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        // Standard transport behaviour: an early press goes back, a later press
        // restarts the current song from the beginning.
        if (Position >= 5)
        {
            Seek(0);
            return;
        }

        // Keep the circular queue in an order where the current track is first. The
        // last item then becomes the track before it, including when wrapping around.
        var queueAligned = AlignQueueWithCurrentTrack();

        if (_history.Count > 0)
        {
            var previous = _history.Pop();
            _ = LoadTrackAsync(previous, autoPlay: true, addToHistory: false);
            OnQueueChanged();
            return;
        }

        // No play history (for example, the queue was started by tapping a track):
        // wrap to the previous item in the queue instead of doing nothing.
        if (queueAligned && _playQueue.Count > 1)
        {
            var previous = _playQueue.LastOrDefault();
            if (previous is not null && !IsSameTrack(previous, CurrentTrack))
            {
                _ = LoadTrackAsync(previous, autoPlay: true, addToHistory: false);
                OnQueueChanged();
                return;
            }
        }

        Seek(0);
    }

    /// <summary>
    /// Rotates the queue so the current track is at its head. The queue is circular, so
    /// its last item is always the track before the current one.
    /// </summary>
    private bool AlignQueueWithCurrentTrack()
    {
        if (CurrentTrack is null || _playQueue.Count == 0)
        {
            return false;
        }

        var items = _playQueue.ToList();
        var index = items.FindIndex(item => IsSameTrack(item, CurrentTrack));
        if (index < 0)
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        _playQueue.Clear();
        foreach (var item in items.Skip(index).Concat(items.Take(index)))
        {
            _playQueue.Enqueue(item);
        }

        return true;
    }

    private static bool IsSameTrack(Music first, Music second)
        => ReferenceEquals(first, second) ||
           (first.Id == second.Id &&
            string.Equals(first.FilePath, second.FilePath, StringComparison.OrdinalIgnoreCase));

    public void TogglePlayPause()
    {
        if (State == PlaybackState.Playing)
        {
            Pause();
        }
        else if (State != PlaybackState.Buffering)
        {
            Resume();
        }
    }

    public void Pause()
    {
        if (State != PlaybackState.Playing) return;

        State = PlaybackState.Paused;
        _ = SafeEngineCallAsync(() => _audioPlayer.PauseAsync());
    }

    public void Resume()
    {
        if (CurrentTrack is null || State == PlaybackState.Playing)
        {
            return;
        }

        if (State == PlaybackState.Stopped)
        {
            // The engine may have been released after Stop(), so reload the file.
            _ = LoadTrackAsync(CurrentTrack, autoPlay: true, addToHistory: false);
            return;
        }

        State = PlaybackState.Playing;
        _ = SafeEngineCallAsync(() => _audioPlayer.PlayAsync());
    }

    public void Stop()
    {
        _loadCancellation?.Cancel();
        State = PlaybackState.Stopped;
        Position = 0;
        _ = SafeEngineCallAsync(() => _audioPlayer.StopAsync());
    }

    public void Seek(double position)
    {
        if (CurrentTrack is null) return;

        var maximum = Duration > 0 ? Duration : Math.Max(0, position);
        var target = Math.Clamp(position, 0, maximum);

        Position = target;
        PositionChanged?.Invoke(target);
        _positionTrustedFrom = DateTime.UtcNow.AddMilliseconds(900);
        _ = SafeEngineCallAsync(() => _audioPlayer.SeekAsync(target));
#if ANDROID
        MediaPlaybackBridge.UpdateProgress();
#endif
    }

    public void SetVolume(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        if (Volume > 0 && IsMuted)
        {
            IsMuted = false;
        }
    }

    public void ToggleMute() => IsMuted = !IsMuted;

    // ------------------------------------------------------------ audio tools

    public void SetPlaybackSpeed(double speed) =>
        PlaybackSpeed = Math.Clamp(speed, 0.5, 2.0);

    public void SetPitchSemitones(int semitones) =>
        Pitch = Math.Pow(2, Math.Clamp(semitones, -12, 12) / 12.0);

    public void SetEqualizerEnabled(bool enabled)
    {
        if (!_audioPlayer.SupportsEqualizer && enabled)
        {
            return;
        }

        IsEqualizerEnabled = enabled;
    }

    public void SetEqualizerPreset(string preset)
    {
        if (!_audioPlayer.SupportsEqualizer || string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        EqualizerPreset = preset;
        IsEqualizerEnabled = true;
        _audioPlayer.SetEqualizerPreset(preset);
    }

    /// <summary>
    /// Ends playback after the supplied duration, or after the current track when
    /// <paramref name="endOfTrack"/> is true. Passing null clears an active timer.
    /// </summary>
    public void SetSleepTimer(TimeSpan? duration, bool endOfTrack = false)
    {
        _sleepTimer?.Dispose();
        _sleepTimer = null;
        _sleepEndsAt = null;
        _sleepAtEndOfTrack = false;

        if (endOfTrack)
        {
            _sleepAtEndOfTrack = true;
            SleepTimerDisplay = "End of track";
            return;
        }

        if (duration is null || duration <= TimeSpan.Zero)
        {
            SleepTimerDisplay = "Off";
            return;
        }

        _sleepEndsAt = DateTimeOffset.UtcNow + duration.Value;
        _sleepTimer = new System.Threading.Timer(
            _ => MainThread.BeginInvokeOnMainThread(OnSleepTimerElapsed),
            null,
            duration.Value,
            Timeout.InfiniteTimeSpan);

        UpdateSleepTimerDisplay();
    }

    private void OnSleepTimerElapsed()
    {
        _sleepTimer?.Dispose();
        _sleepTimer = null;
        _sleepEndsAt = null;
        _sleepAtEndOfTrack = false;

        if (State == PlaybackState.Playing)
        {
            Pause();
        }
        else if (State == PlaybackState.Buffering)
        {
            Stop();
        }

        SleepTimerDisplay = "Off";
    }

    private void UpdateSleepTimerDisplay()
    {
        if (_sleepEndsAt is not { } endsAt)
        {
            return;
        }

        var remaining = endsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            OnSleepTimerElapsed();
            return;
        }

        SleepTimerDisplay = remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.ToString(@"m\:ss");
    }

    public void ToggleShuffle() => IsShuffle = !IsShuffle;

    public void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
    }

    public void UpdatePosition(double position)
    {
        Position = position;
        PositionChanged?.Invoke(position);
    }

    private async Task LoadTrackAsync(Music track, bool autoPlay, bool addToHistory)
    {
        if (addToHistory && CurrentTrack is not null && !ReferenceEquals(CurrentTrack, track))
        {
            _history.Push(CurrentTrack);
            OnPropertyChanged(nameof(PlayHistory));
        }

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _loadCancellation, cancellation);

        // Cancelling the old load also prevents a slow decoder from replacing the
        // track the user selected most recently.
        previousCancellation?.Cancel();

        CurrentTrack = track;
        State = PlaybackState.Buffering;
        PlaybackError = null;
        Position = 0;
        _positionTrustedFrom = DateTime.MinValue;
        Duration = Math.Max(0, track.Duration.TotalSeconds);
        TrackChanged?.Invoke(track);

        try
        {
            var reportedDuration = await _audioPlayer.LoadAsync(
                track.FilePath,
                autoPlay,
                cancellation.Token);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (reportedDuration > TimeSpan.Zero)
            {
                Duration = reportedDuration.TotalSeconds;
            }

            // Reapply enhancements after every source change: Android rebuilds the native
            // session per track, and the engine keeps these values internally as well.
            _audioPlayer.SetPlaybackRate(PlaybackSpeed);
            _audioPlayer.SetPitch(Pitch);
            _audioPlayer.SetEqualizerEnabled(IsEqualizerEnabled);
            if (IsEqualizerEnabled)
            {
                _audioPlayer.SetEqualizerPreset(EqualizerPreset);
            }

            State = autoPlay ? PlaybackState.Playing : PlaybackState.Paused;
        }
        catch (OperationCanceledException)
        {
            // A newer track was selected, or the user stopped playback.
        }
        catch (Exception ex)
        {
            HandleAudioFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task SafeEngineCallAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // Stop/cancel is expected during track changes.
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() => HandleAudioFailure(ex));
        }
    }

    private void HandleAudioFailure(Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(exception);

        PlaybackError = CurrentTrack is null
            ? "Playback failed."
            : $"Could not play \"{CurrentTrack.Name}\": {exception.Message}";

        State = PlaybackState.Stopped;
        StopPositionTimer();
    }

    private void OnAudioPlayerPlaybackEnded(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(HandlePlaybackEnded);
    }

    private void OnAudioPlayerFailed(object? sender, AudioPlayerFailureEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => HandleAudioFailure(e.Exception));
    }

    /// <summary>
    /// A headset, Bluetooth band/speaker or USB output was removed. Pausing here means the
    /// track never leaks out of the phone speaker; the song resumes from the same spot once
    /// the user presses play again.
    /// </summary>
    private void OnAudioDeviceDisconnected(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (State)
            {
                case PlaybackState.Playing:
                    Pause();
                    break;
                case PlaybackState.Buffering:
                    Stop();
                    break;
            }
        });
    }

    private void HandlePlaybackEnded()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        if (_sleepAtEndOfTrack)
        {
            _sleepTimer?.Dispose();
            _sleepTimer = null;
            _sleepAtEndOfTrack = false;
            SleepTimerDisplay = "Off";
            Stop();
            return;
        }

        if (RepeatMode == RepeatMode.One)
        {
            _ = LoadTrackAsync(CurrentTrack, autoPlay: true, addToHistory: false);
            return;
        }

        if (_playQueue.Count == 0)
        {
            if (RepeatMode == RepeatMode.All)
            {
                _ = LoadTrackAsync(CurrentTrack, autoPlay: true, addToHistory: false);
            }
            else
            {
                Stop();
            }

            return;
        }

        // With repeat off, stop after the queue has played through once instead of
        // rotating the same queue forever.
        if (RepeatMode == RepeatMode.Off &&
            _queueCycleLength > 0 &&
            _tracksPlayedInCycle >= _queueCycleLength)
        {
            Stop();
            return;
        }

        PlayNext();
    }

    private void StartPositionTimer()
    {
        if (_positionTimer is null)
        {
            var dispatcher = Microsoft.Maui.Controls.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            _positionTimer = dispatcher.CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(400);
            _positionTimer.Tick += OnPositionTimerTick;
        }

        if (!_positionTimer.IsRunning)
        {
            _positionTimer.Start();
        }
    }

    private void StopPositionTimer() => _positionTimer?.Stop();

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (State != PlaybackState.Playing)
        {
            return;
        }

        var position = _audioPlayer.PositionSeconds;
        if (double.IsFinite(position) && position >= 0 && DateTime.UtcNow >= _positionTrustedFrom)
        {
            UpdatePosition(position);
        }

        var engineDuration = _audioPlayer.DurationSeconds;
        if (engineDuration > 0 && Math.Abs(Duration - engineDuration) > 0.5)
        {
            Duration = engineDuration;
        }

        UpdateSleepTimerDisplay();
#if ANDROID
        MediaPlaybackBridge.UpdateProgress();
#endif
    }

    private void ExtendQueueCycleForAddedTracks(int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (_queueCycleLength <= 0)
        {
            _queueCycleLength = _tracksPlayedInCycle + count;
        }
        else
        {
            _queueCycleLength += count;
        }
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(PlayQueue));
        OnPropertyChanged(nameof(PlayHistory));
        QueueChanged?.Invoke();
    }

    partial void OnPlaybackErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPlaybackError));
    }

    partial void OnStateChanged(PlaybackState value)
    {
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseIcon));
        StateChanged?.Invoke(value);

        if (value == PlaybackState.Playing)
        {
            StartPositionTimer();
        }
        else
        {
            StopPositionTimer();
        }

#if ANDROID
        MediaPlaybackBridge.Refresh();
#endif
    }

    partial void OnVolumeChanged(double value)
    {
        _audioPlayer.SetVolume(IsMuted ? 0 : value);
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        Preferences.Default.Set(PlaybackSpeedKey, value);
        _audioPlayer.SetPlaybackRate(value);
        OnPropertyChanged(nameof(SpeedDisplay));
    }

    partial void OnPitchChanged(double value)
    {
        Preferences.Default.Set(PitchKey, value);
        _audioPlayer.SetPitch(value);
        OnPropertyChanged(nameof(PitchDisplay));
        OnPropertyChanged(nameof(PitchSemitones));
    }

    partial void OnIsEqualizerEnabledChanged(bool value)
    {
        Preferences.Default.Set(EqualizerEnabledKey, value);
        _audioPlayer.SetEqualizerEnabled(value);
        OnPropertyChanged(nameof(EqualizerDisplay));
    }

    partial void OnEqualizerPresetChanged(string value)
    {
        Preferences.Default.Set(EqualizerPresetKey, value);
        _audioPlayer.SetEqualizerPreset(value);
        OnPropertyChanged(nameof(EqualizerDisplay));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(MuteIcon));
        _audioPlayer.SetVolume(value ? 0 : Volume);
    }

    partial void OnIsShuffleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShuffleGlyph));
    }

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatGlyph));
    }
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    Buffering
}

public enum RepeatMode
{
    Off,
    All,
    One
}
