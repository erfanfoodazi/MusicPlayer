using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// Edits every stored tag of one audio file: title, artist, album, album artist, year,
/// track/disc numbers, genres and the cover image. Saving writes to the file, re-reads it,
/// refreshes the shared library and persists the snapshot.
/// </summary>
public partial class TagEditorViewModel : BaseViewModel
{
    private const long MaxCoverBytes = 12 * 1024 * 1024;

    private readonly TagEditorSession _session;
    private readonly MusicFileService _fileService;
    private Music? _track;
    private Music? _loadedTrack;
    private byte[]? _coverBytes;
    private string? _coverMimeType;
    private bool _removeCover;
    private string _initialSignature = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _titleText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _artistText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _albumText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _albumArtistText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _yearText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _trackNumberText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _discNumberText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _genresText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverPreview))]
    private ImageSource? _coverPreview;

    [ObservableProperty]
    private string _detailsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _hasLoadedTrack;

    public TagEditorViewModel(TagEditorSession session, MusicFileService fileService)
    {
        _session = session;
        _fileService = fileService;
        Title = "Edit tags";

        // IsBusy lives on the base view model, so watch its change notification instead of
        // declaring a partial method for a property this class does not own.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        };
    }

    public bool HasCoverPreview => CoverPreview is not null;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasChanges => HasLoadedTrack &&
                              !string.Equals(BuildSignature(), _initialSignature, StringComparison.Ordinal);

    // Saving is always available while a track is open: re-writing identical tags is
    // harmless and the explicit action is less surprising than a silently disabled button.
    public bool CanSave => HasLoadedTrack && !IsBusy;

    /// <summary>
    /// Loads the session's track. Safe to call on every appearance: it only rebuilds the
    /// form when the session points at a different file, so navigating back and forth does
    /// not wipe edits or leave the previous song's tags on screen.
    /// </summary>
    public void Load()
    {
        var track = _session.Track;
        if (track is null)
        {
            _track = null;
            _loadedTrack = null;
            HasLoadedTrack = false;
            StatusMessage = "This track is no longer available. Go back and choose it again.";
            return;
        }

        if (ReferenceEquals(track, _loadedTrack))
        {
            return;
        }

        _track = track;
        _loadedTrack = track;
        _coverBytes = null;
        _coverMimeType = null;
        _removeCover = false;

        TitleText = track.Name;
        ArtistText = track.Artist?.Name ?? string.Empty;
        AlbumText = track.Album?.Title ?? string.Empty;
        AlbumArtistText = track.AlbumArtist;
        YearText = track.Year is > 0 ? track.Year.Value.ToString() : string.Empty;
        TrackNumberText = track.TrackNumber is > 0 ? track.TrackNumber.Value.ToString() : string.Empty;
        DiscNumberText = track.DiscNumber is > 0 ? track.DiscNumber.Value.ToString() : string.Empty;
        GenresText = string.Join(", ", track.Genres.Select(g => g.Name));

        DetailsText = BuildDetails(track);

        CoverPreview = track.ArtworkPath is { Length: > 0 } artworkPath && File.Exists(artworkPath)
            ? ImageSource.FromFile(artworkPath)
            : null;

        _initialSignature = BuildSignature();
        HasLoadedTrack = true;
        StatusMessage = string.Empty;

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
    }

    private string BuildSignature() => string.Join(
        '\u001f',
        TitleText,
        ArtistText,
        AlbumText,
        AlbumArtistText,
        YearText,
        TrackNumberText,
        DiscNumberText,
        GenresText,
        _removeCover ? "remove-cover" : _coverBytes is null ? "keep-cover" : "new-cover");

    private static string BuildDetails(Music track)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Format))
        {
            parts.Add(track.Format.ToUpperInvariant());
        }

        if (track.AudioFileInfo is { BitrateKbps: > 0 } info)
        {
            parts.Add($"{info.BitrateKbps} kbps");
        }

        if (track.AudioFileInfo is { SampleRateHz: > 0 } rate)
        {
            parts.Add($"{rate.SampleRateHz / 1000.0:0.#} kHz");
        }

        if (track.Duration > TimeSpan.Zero)
        {
            parts.Add(track.Duration.TotalHours >= 1
                ? track.Duration.ToString(@"h\:mm\:ss")
                : track.Duration.ToString(@"m\:ss"));
        }

        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task PickCoverAsync()
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Choose a cover image",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, ["image/*"] },
                    { DevicePlatform.WinUI, [".jpg", ".jpeg", ".png", ".webp"] },
                }),
            };

            var result = await FilePicker.Default.PickAsync(options);
            if (result is null)
            {
                return;
            }

            using var stream = await result.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            if (buffer.Length == 0)
            {
                StatusMessage = "That image could not be read.";
                return;
            }

            if (buffer.Length > MaxCoverBytes)
            {
                StatusMessage = "That image is too large (12 MB maximum).";
                return;
            }

            _coverBytes = buffer.ToArray();
            _coverMimeType = GuessMimeType(result.FileName);
            _removeCover = false;
            CoverPreview = ImageSource.FromStream(() => new MemoryStream(_coverBytes));
            StatusMessage = "New cover ready — tap Save to write it into the file.";
            RaiseDirtyState();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not use that image: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveCover()
    {
        _coverBytes = null;
        _coverMimeType = null;
        _removeCover = true;
        CoverPreview = null;
        StatusMessage = "Cover will be removed when you tap Save.";
        RaiseDirtyState();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var track = _track;
        if (track is null)
        {
            if (Shell.Current is { } shellWhenEmpty)
            {
                await shellWhenEmpty.GoToAsync("..");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(TitleText))
        {
            StatusMessage = "A track needs a title. Type one or press Cancel.";
            return;
        }

        if (!TryParseNumber(YearText, 0, 9999, "Year", out var year) ||
            !TryParseNumber(TrackNumberText, 0, 99999, "Track number", out var trackNumber) ||
            !TryParseNumber(DiscNumberText, 0, 99999, "Disc number", out var discNumber))
        {
            return;
        }

        var page = Shell.Current?.CurrentPage;
        if (!await _fileService.EnsureFullFileAccessAsync(page, "edit music tags"))
        {
            StatusMessage = "Tag editing needs file access. Grant it in Settings, then try again.";
            return;
        }

        var edit = new MusicTagEdit
        {
            Title = TitleText.Trim(),
            Artist = ArtistText.Trim(),
            Album = AlbumText.Trim(),
            AlbumArtist = AlbumArtistText.Trim(),
            Year = year,
            TrackNumber = trackNumber,
            DiscNumber = discNumber,
            Genres = ParseGenres(GenresText),
            CoverImageBytes = _coverBytes,
            CoverMimeType = _coverMimeType,
            RemoveCover = _removeCover && _coverBytes is null,
        };

        try
        {
            IsBusy = true;
            StatusMessage = "Saving tags…";

            await _fileService.SaveTagsAsync(track, edit);

            // Keep the refreshed instance in the session so reopening the editor shows the
            // values that were actually written to the file.
            _loadedTrack = null;
            _session.Track = null;
            Haptics.Tap();

            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync("..");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseDirtyState()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnYearTextChanged(string value) => RaiseDirtyState();
    partial void OnTrackNumberTextChanged(string value) => RaiseDirtyState();
    partial void OnDiscNumberTextChanged(string value) => RaiseDirtyState();
    partial void OnGenresTextChanged(string value) => RaiseDirtyState();
    partial void OnTitleTextChanged(string value) => RaiseDirtyState();
    partial void OnArtistTextChanged(string value) => RaiseDirtyState();
    partial void OnAlbumTextChanged(string value) => RaiseDirtyState();
    partial void OnAlbumArtistTextChanged(string value) => RaiseDirtyState();

    private bool TryParseNumber(string? text, int minimum, int maximum, string label, out int value)
    {
        value = 0;

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return true; // empty clears the tag
        }

        if (!int.TryParse(trimmed, out var parsed) || parsed < minimum || parsed > maximum)
        {
            StatusMessage = $"{label} must be a number between {minimum} and {maximum}.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static List<string> ParseGenres(string? text)
        => (text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string GuessMimeType(string? fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };
}
