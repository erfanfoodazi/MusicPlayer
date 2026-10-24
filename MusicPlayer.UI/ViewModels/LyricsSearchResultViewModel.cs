using Domain.Models;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// Presentation wrapper for one LRCLIB preview result. The underlying domain result is kept
/// in <see cref="Result"/> and is only applied when the user taps "Use".
/// </summary>
public sealed class LyricsSearchResultViewModel
{
    public LyricsSearchResultViewModel(LyricsSearchResult result)
    {
        Result = result;
    }

    public LyricsSearchResult Result { get; }

    public string Title => string.IsNullOrWhiteSpace(Result.TrackName)
        ? "Unknown title"
        : Result.TrackName;

    public string Artist => string.IsNullOrWhiteSpace(Result.ArtistName)
        ? "Unknown artist"
        : Result.ArtistName;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Result.AlbumName))
            {
                parts.Add(Result.AlbumName);
            }

            if (!string.IsNullOrWhiteSpace(Result.DurationText))
            {
                parts.Add(Result.DurationText);
            }

            return string.Join(" • ", parts);
        }
    }

    public string Kind => Result.Instrumental
        ? "Instrumental"
        : Result.HasSyncedLyrics ? "Synced" : "Plain";
}
