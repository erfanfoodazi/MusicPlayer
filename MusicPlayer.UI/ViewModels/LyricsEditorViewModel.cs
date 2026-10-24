using Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// Text editor for the current lyrics (plain or LRC). Saving stores the text in the app's
/// lyrics cache as the user's own version, which immediately becomes the preferred version
/// for that track.
/// </summary>
public partial class LyricsEditorViewModel : BaseViewModel
{
    private readonly LyricsEditorSession _session;
    private readonly LyricsService _lyricsService;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _trackTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public LyricsEditorViewModel(LyricsEditorSession session, LyricsService lyricsService)
    {
        _session = session;
        _lyricsService = lyricsService;
        Title = "Edit lyrics";
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public void Load()
    {
        TrackTitle = _session.Track?.Name ?? string.Empty;
        RawText = _session.InitialText;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var text = RawText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            StatusMessage = "Lyrics cannot be empty. Tap Cancel to keep the current version.";
            return;
        }

        var lyrics = LrcParser.Parse(text, LyricsSource.Manual);

        if (_session.Track is { } track)
        {
            try
            {
                await _lyricsService.SaveCachedLyricsAsync(track, lyrics);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save on this device: {ex.Message}";
                return;
            }
        }

        _session.Result = lyrics;
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        _session.Result = null;
        await Shell.Current.GoToAsync("..");
    }
}
