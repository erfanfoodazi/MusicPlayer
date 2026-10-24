using System.Collections.ObjectModel;
using System.ComponentModel;
using Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// Drives the Lyrics page:
///  - loads embedded / local .lrc / cached lyrics for the current track,
///  - offers LRCLIB search with preview and explicit confirmation,
///  - highlights the active line (and word, for enhanced LRC) from PlayerService.Position,
///  - lets the user nudge the sync offset, edit, import/export, pick a version and save the
///    lyrics into the music file itself.
///
/// The position notifications come from the player's 400 ms timer. There is no independent
/// lyrics clock, so seeking or pausing can never make the text drift out of sync.
/// </summary>
public partial class LyricsViewModel : BaseViewModel
{
    private const string AutoFetchLyricsKey = "settings.autoFetchLyrics";
    private const double OffsetStepSeconds = 0.1;

    private readonly PlayerService _playerService;
    private readonly LyricsService _lyricsService;
    private readonly LyricsFileService _lyricsFileService;
    private readonly MusicFileService _fileService;
    private CancellationTokenSource? _loadCancellation;
    private Music? _loadedTrack;
    private Lyrics? _currentLyrics;
    private int _activeWordIndex = -1;
    private double _sessionOffsetSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackTitle))]
    [NotifyPropertyChangedFor(nameof(TrackArtist))]
    [NotifyPropertyChangedFor(nameof(ArtworkSource))]
    private Music? _currentTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsPlainMode))]
    private bool _hasLyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShiftOffset))]
    private bool _hasSyncedLyrics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainMode))]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    private bool _isSyncedMode = true;

    [ObservableProperty]
    private string _plainText = string.Empty;

    [ObservableProperty]
    private string _sourceLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<LyricsLineViewModel> _lines = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchResults))]
    private ObservableCollection<LyricsSearchResultViewModel> _searchResults = [];

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isSearchPanelVisible;

    private int _activeLineIndex = -1;

    /// <summary>Index of the highlighted synced line, or -1 before the first timestamp.</summary>
    public int ActiveLineIndex
    {
        get => _activeLineIndex;
        private set => SetProperty(ref _activeLineIndex, value);
    }

    public LyricsViewModel(
        PlayerService playerService,
        LyricsService lyricsService,
        LyricsFileService lyricsFileService,
        MusicFileService fileService)
    {
        _playerService = playerService;
        _lyricsService = lyricsService;
        _lyricsFileService = lyricsFileService;
        _fileService = fileService;
        Title = "Lyrics";

        CurrentTrack = _playerService.CurrentTrack;
        _playerService.PropertyChanged += OnPlayerServicePropertyChanged;
    }

    /// <summary>Set by the page so position ticks are ignored while the page is not visible.</summary>
    public bool IsPageVisible { get; set; }

    public bool IsPlainMode => HasLyrics && !IsSyncedMode;

    public bool ShowEmptyState => !HasLyrics && !IsBusy;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanShiftOffset => HasSyncedLyrics;

    /// <summary>True when any lyrics are shown; drives the "more actions" button.</summary>
    public bool HasAnyLyrics => HasLyrics;

    public string TrackTitle => CurrentTrack?.Name ?? "Nothing playing";

    public string TrackArtist => CurrentTrack is null
        ? "Play a song to see its lyrics"
        : CurrentTrack.ArtistName;

    /// <summary>Low-opacity ambient cover behind the lyrics, like Oto's immersive view.</summary>
    public ImageSource ArtworkSource => !string.IsNullOrWhiteSpace(CurrentTrack?.ArtworkPath)
        ? ImageSource.FromFile(CurrentTrack!.ArtworkPath!)
        : ImageSource.FromFile("placeholder_album.png");

    public string ModeButtonText => IsSyncedMode ? "Synced" : "Plain";

    /// <summary>Raised when the active synced line changes; the page scrolls the list.</summary>
    public event EventHandler<int>? ScrollToLineRequested;

    /// <summary>Called when the page appears. Keeps existing lyrics for the same track.</summary>
    public void Refresh(bool force = false)
    {
        var track = _playerService.CurrentTrack;

        if (!force &&
            ReferenceEquals(track, _loadedTrack) &&
            (HasLyrics || IsSearchPanelVisible || !string.IsNullOrWhiteSpace(StatusMessage)))
        {
            UpdateActiveLine(_playerService.Position, force: true);
            return;
        }

        _ = LoadLyricsAsync(track);
    }

    private async Task LoadLyricsAsync(Music? track)
    {
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        _loadedTrack = track;
        CurrentTrack = track;
        ActiveLineIndex = -1;
        _activeLineIndex = -1;
        _activeWordIndex = -1;
        _currentLyrics = null;
        HasLyrics = false;
        HasSyncedLyrics = false;
        IsSyncedMode = false;
        IsSearchPanelVisible = false;
        SearchResults = [];
        Lines = [];
        PlainText = string.Empty;
        SourceLabel = string.Empty;
        StatusMessage = string.Empty;
        RaiseEmptyState();

        if (track is null)
        {
            IsBusy = false;
            StatusMessage = "Nothing is playing.";
            RaiseEmptyState();
            return;
        }

        IsBusy = true;
        StatusMessage = "Looking for lyrics…";
        RaiseEmptyState();

        try
        {
            var lyrics = await _lyricsService.FindLocalLyricsAsync(track, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(track, CurrentTrack))
            {
                return;
            }

            if (lyrics is not null)
            {
                ApplyLyrics(lyrics);
                return;
            }

            StatusMessage = "No embedded or local .lrc lyrics were found.";

            // The settings switch "Auto-fetch lyrics" makes the first step easier, but the
            // result is still only a preview until the user taps Use.
            if (Preferences.Default.Get(AutoFetchLyricsKey, true))
            {
                IsBusy = false;
                RaiseEmptyState();
                await SearchOnlineAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A new track or a new load replaced this one.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load lyrics: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsBusy = false;
                _loadCancellation = null;
                RaiseEmptyState();
            }
        }
    }

    private void ApplyLyrics(Lyrics lyrics)
    {
        _currentLyrics = lyrics;

        var lines = lyrics.Lines
            .OrderBy(line => line.StartTime)
            .Select(line => new LyricsLineViewModel(line))
            .ToList();

        Lines = new ObservableCollection<LyricsLineViewModel>(lines);
        PlainText = lyrics.Text;
        HasLyrics = lines.Count > 0 || !string.IsNullOrWhiteSpace(lyrics.Text);
        HasSyncedLyrics = lines.Count > 0 && lyrics.Type != LyricsType.Plain;
        IsSyncedMode = HasSyncedLyrics;
        SourceLabel = lyrics.Source switch
        {
            LyricsSource.EmbeddedMetadata => "Embedded lyrics",
            LyricsSource.LocalLrcFile => "Local .lrc file",
            LyricsSource.LrcLib => "LRCLIB · saved on this device",
            LyricsSource.Manual => "Edited / imported · saved on this device",
            _ => string.Empty,
        };

        StatusMessage = string.Empty;
        IsSearchPanelVisible = false;
        _activeLineIndex = -1;
        ActiveLineIndex = -1;
        _activeWordIndex = -1;
        _sessionOffsetSeconds = 0;

        RaiseEmptyState();
        OnPropertyChanged(nameof(IsPlainMode));
        OnPropertyChanged(nameof(ModeButtonText));
        OnPropertyChanged(nameof(HasAnyLyrics));

        // Put the highlight on the line that is playing right now.
        UpdateActiveLine(_playerService.Position, force: true);
    }

    /// <summary>Called by the lyrics page when the editor handed back saved text.</summary>
    public void ApplyExternalLyrics(Lyrics lyrics)
    {
        ApplyLyrics(lyrics);
        StatusMessage = "Lyrics updated.";
    }

    private void OnPlayerServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerService.Position))
        {
            if (!IsPageVisible || !IsSyncedMode)
            {
                return;
            }

            var position = _playerService.Position;
            if (MainThread.IsMainThread)
            {
                UpdateActiveLine(position);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => UpdateActiveLine(position));
            }

            return;
        }

        if (e.PropertyName == nameof(PlayerService.CurrentTrack))
        {
            var track = _playerService.CurrentTrack;
            if (MainThread.IsMainThread)
            {
                HandleTrackChanged(track);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => HandleTrackChanged(track));
            }
        }
    }

    private void HandleTrackChanged(Music? track)
    {
        // Do not load or search while the page is hidden; mark the data as stale and let
        // the next OnAppearing pick up the new track.
        _loadedTrack = null;
        if (!IsPageVisible)
        {
            return;
        }

        _ = LoadLyricsAsync(track);
    }

    /// <summary>
    /// Finds the last line whose start time is at or before the playback position. Before
    /// the first timestamp no line is highlighted; during instrumental gaps the previous
    /// line stays highlighted, which is how dedicated lyric players behave.
    /// </summary>
    private void UpdateActiveLine(double positionSeconds, bool force = false)
    {
        if (!IsSyncedMode || Lines.Count == 0 ||
            double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds))
        {
            return;
        }

        var position = TimeSpan.FromSeconds(Math.Max(0, positionSeconds));
        var index = FindLineIndex(position);

        if (!force && index == _activeLineIndex)
        {
            // The line stays the same but the word highlight may have moved on.
            UpdateActiveWords(index, position);
            return;
        }

        if (_activeLineIndex >= 0 && _activeLineIndex < Lines.Count)
        {
            Lines[_activeLineIndex].IsActive = false;
            Lines[_activeLineIndex].ClearWordHighlights();
        }

        _activeLineIndex = index;
        _activeWordIndex = -1;

        if (index >= 0 && index < Lines.Count)
        {
            Lines[index].IsActive = true;
        }

        ActiveLineIndex = index;
        UpdateActiveWords(index, position);
        ScrollToLineRequested?.Invoke(this, index);
    }

    private void UpdateActiveWords(int lineIndex, TimeSpan position)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count)
        {
            return;
        }

        var words = Lines[lineIndex].Words;
        if (words.Count == 0)
        {
            return;
        }

        var wordIndex = -1;
        var low = 0;
        var high = words.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (words[middle].StartTime <= position)
            {
                wordIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (wordIndex == _activeWordIndex)
        {
            return;
        }

        _activeWordIndex = wordIndex;
        for (var i = 0; i < words.Count; i++)
        {
            words[i].IsActive = i == wordIndex;
        }
    }

    private int FindLineIndex(TimeSpan position)
    {
        var low = 0;
        var high = Lines.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (Lines[middle].StartTime <= position)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    [RelayCommand]
    private void ToggleMode()
    {
        if (!HasSyncedLyrics)
        {
            return;
        }

        IsSyncedMode = !IsSyncedMode;

        if (IsSyncedMode)
        {
            UpdateActiveLine(_playerService.Position, force: true);
        }
    }

    /// <summary>Seeking by tapping a lyric line, just like Oto Music.</summary>
    [RelayCommand]
    private void SeekToLine(LyricsLineViewModel? line)
    {
        if (line is null || line.StartTime < TimeSpan.Zero)
        {
            return;
        }

        _playerService.Seek(line.StartTime.TotalSeconds);

        // Move the highlight immediately instead of waiting for the next 400 ms position tick.
        UpdateActiveLine(_playerService.Position, force: true);
        Haptics.Tap();
    }

    // ------------------------------------------------------------- sync offset

    [RelayCommand]
    private Task ShiftEarlierAsync() => NudgeOffsetAsync(TimeSpan.FromSeconds(-OffsetStepSeconds));

    [RelayCommand]
    private Task ShiftLaterAsync() => NudgeOffsetAsync(TimeSpan.FromSeconds(OffsetStepSeconds));

    /// <summary>
    /// Moves every timestamp by <paramref name="delta"/> ("-0.1s" makes lyrics appear
    /// sooner) and keeps the adjusted version, so the change survives app restarts.
    /// </summary>
    private async Task NudgeOffsetAsync(TimeSpan delta)
    {
        if (_currentLyrics is not { } lyrics || !HasSyncedLyrics)
        {
            return;
        }

        LrcParser.Shift(lyrics, delta);
        _sessionOffsetSeconds += delta.TotalSeconds;
        RebuildLinesFromCurrent();
        UpdateActiveLine(_playerService.Position, force: true);

        StatusMessage = Math.Abs(_sessionOffsetSeconds) < 0.0005
            ? "Lyrics are back in sync."
            : $"Sync offset {_sessionOffsetSeconds:+0.0;-0.0}s · saved";

        if (CurrentTrack is { } track)
        {
            await SaveCachedLyricsAsync(track, lyrics);
        }
    }

    [RelayCommand]
    private async Task ResetOffsetAsync()
    {
        if (_currentLyrics is not { } lyrics || !HasSyncedLyrics)
        {
            return;
        }

        if (Math.Abs(_sessionOffsetSeconds) < 0.0005)
        {
            StatusMessage = "Lyrics are already in sync.";
            return;
        }

        LrcParser.Shift(lyrics, TimeSpan.FromSeconds(-_sessionOffsetSeconds));
        _sessionOffsetSeconds = 0;
        RebuildLinesFromCurrent();
        UpdateActiveLine(_playerService.Position, force: true);
        StatusMessage = "Sync offset reset to 0.0s.";

        if (CurrentTrack is { } track)
        {
            await SaveCachedLyricsAsync(track, lyrics);
        }
    }

    private void RebuildLinesFromCurrent()
    {
        if (_currentLyrics is null)
        {
            return;
        }

        Lines = new ObservableCollection<LyricsLineViewModel>(
            _currentLyrics.Lines
                .OrderBy(line => line.StartTime)
                .Select(line => new LyricsLineViewModel(line)));

        PlainText = _currentLyrics.Text;
        _activeLineIndex = -1;
        ActiveLineIndex = -1;
        _activeWordIndex = -1;
        OnPropertyChanged(nameof(IsPlainMode));
    }

    // ----------------------------------------------------------------- versions

    public Task<IReadOnlyList<LyricsVersionInfo>> GetVersionsAsync()
    {
        var track = CurrentTrack;
        return track is null
            ? Task.FromResult<IReadOnlyList<LyricsVersionInfo>>([])
            : _lyricsService.GetAvailableVersionsAsync(track);
    }

    public async Task ApplyVersionAsync(LyricsVersionInfo version)
    {
        var track = CurrentTrack;
        if (track is null)
        {
            return;
        }

        var lyrics = await _lyricsService.LoadVersionAsync(track, version.Source);
        if (lyrics is null)
        {
            StatusMessage = "That version is not available for this track.";
            return;
        }

        await _lyricsService.SetPreferenceAsync(track, version.Source);
        ApplyLyrics(lyrics);
        StatusMessage = $"Using: {version.Label}";
    }

    /// <summary>Clears the pinned choice and follows the automatic priority again.</summary>
    public async Task ApplyAutomaticVersionAsync()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            return;
        }

        _lyricsService.ClearPreference(track);
        var lyrics = await _lyricsService.FindLocalLyricsAsync(track);

        if (lyrics is null)
        {
            StatusMessage = "No local lyrics were found for this track.";
            return;
        }

        ApplyLyrics(lyrics);
        StatusMessage = "Using the automatic version.";
    }

    // ----------------------------------------------------------- editing files

    [RelayCommand]
    private async Task EditLyricsAsync()
    {
        var track = CurrentTrack;
        if (track is null || _currentLyrics is null)
        {
            StatusMessage = "There are no lyrics to edit yet.";
            return;
        }

        var session = ServiceHelper.GetRequiredService<LyricsEditorSession>();
        session.Track = track;
        session.InitialText = _currentLyrics.Lines.Count > 0
            ? LrcParser.ToLrc(_currentLyrics)
            : _currentLyrics.Text;
        session.Result = null;

        await Shell.Current.GoToAsync(Routes.LyricsEditor);
    }

    [RelayCommand]
    private async Task ImportLrcAsync()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            StatusMessage = "Nothing is playing.";
            return;
        }

        var text = await _lyricsFileService.ImportLrcTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lyrics = LrcParser.Parse(text, LyricsSource.Manual);
        if (lyrics.Lines.Count == 0 && string.IsNullOrWhiteSpace(lyrics.Text))
        {
            StatusMessage = "That file contains no lyrics.";
            return;
        }

        ApplyLyrics(lyrics);
        await SaveCachedLyricsAsync(track, lyrics);
        StatusMessage = lyrics.Lines.Count > 0
            ? "Imported synced lyrics and saved them on this device."
            : "Imported plain lyrics and saved them on this device.";
    }

    [RelayCommand]
    private async Task ExportLrcAsync()
    {
        var track = CurrentTrack;
        if (track is null || _currentLyrics is null)
        {
            StatusMessage = "There are no lyrics to export yet.";
            return;
        }

        var text = _currentLyrics.Lines.Count > 0
            ? LrcParser.ToLrc(_currentLyrics)
            : _currentLyrics.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "There are no lyrics to export yet.";
            return;
        }

        var fileName = LyricsFileService.SuggestFileName(track.Name, track.ArtistName);
        var exported = await _lyricsFileService.ExportLrcAsync(fileName, text);
        StatusMessage = exported ? $"Exported {fileName}." : "Export cancelled.";
    }

    [RelayCommand]
    private async Task SaveLyricsToFileAsync()
    {
        var track = CurrentTrack;
        if (track is null || _currentLyrics is null)
        {
            return;
        }

        var page = Shell.Current?.CurrentPage;
        if (!await _fileService.EnsureFullFileAccessAsync(page, "save lyrics into the music file"))
        {
            return;
        }

        var text = _currentLyrics.Lines.Count > 0
            ? LrcParser.ToLrc(_currentLyrics)
            : _currentLyrics.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "There are no lyrics to save.";
            return;
        }

        try
        {
            IsBusy = true;
            var refreshed = await _fileService.SaveLyricsToFileAsync(track, text);

            // The library now holds a refreshed Music instance carrying the new embedded
            // lyrics; keep tracking it so later reloads pick the embedded version up.
            if (ReferenceEquals(_loadedTrack, track))
            {
                _loadedTrack = refreshed;
                if (ReferenceEquals(CurrentTrack, track))
                {
                    CurrentTrack = refreshed;
                }
            }

            StatusMessage = "Lyrics saved inside the music file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save lyrics: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseEmptyState();
        }
    }

    // ------------------------------------------------------------ online search

    [RelayCommand]
    private async Task SearchOnlineAsync()
    {
        IsSearchPanelVisible = true;

        if (IsSearching)
        {
            return;
        }

        var track = CurrentTrack ?? _playerService.CurrentTrack;
        if (track is null)
        {
            StatusMessage = "Nothing is playing.";
            return;
        }

        IsSearching = true;
        SearchResults = [];
        StatusMessage = "Searching LRCLIB…";

        try
        {
            var results = await _lyricsService.SearchOnlineAsync(track);

            if (!ReferenceEquals(track, CurrentTrack))
            {
                return;
            }

            SearchResults = new ObservableCollection<LyricsSearchResultViewModel>(
                results.Select(result => new LyricsSearchResultViewModel(result)));

            StatusMessage = SearchResults.Count == 0
                ? "No matches found. Check the title and artist tags, then try again."
                : $"{SearchResults.Count} matches — tap Use to preview one.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void ApplySearchResult(LyricsSearchResultViewModel? result)
    {
        var track = CurrentTrack;
        if (result is null || track is null)
        {
            return;
        }

        var lyrics = _lyricsService.CreateFromSearchResult(result.Result);

        if (lyrics.Lines.Count == 0 && string.IsNullOrWhiteSpace(lyrics.Text))
        {
            StatusMessage = "That LRCLIB result contains no lyrics. Try another match.";
            return;
        }

        // Applying is the explicit confirmation step; only now do we replace what is shown
        // and write the result into the offline cache.
        ApplyLyrics(lyrics);
        StatusMessage = "Lyrics applied from LRCLIB and saved for offline use.";
        _ = SaveCachedLyricsAsync(track, lyrics);
    }

    [RelayCommand]
    private void CloseSearchPanel() => IsSearchPanelVisible = false;

    private async Task SaveCachedLyricsAsync(Music track, Lyrics lyrics)
    {
        try
        {
            await _lyricsService.SaveCachedLyricsAsync(track, lyrics);
        }
        catch
        {
            // Caching is an optimization; a read-only app data folder must not break the UI.
        }
    }

    private void RaiseEmptyState() => OnPropertyChanged(nameof(ShowEmptyState));
}
