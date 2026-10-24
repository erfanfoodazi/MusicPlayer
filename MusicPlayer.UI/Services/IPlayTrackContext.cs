using System.Windows.Input;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Implemented by page view models that know which list a track was tapped in.
/// This lets the shared TrackItem control start playback with the correct queue
/// (library, album, artist, playlist, or search results) instead of a one-item queue.
/// </summary>
public interface IPlayTrackContext
{
    /// <summary>Starts playback without leaving the current page.</summary>
    ICommand PlayTrackCommand { get; }

    /// <summary>Starts playback and opens the Now Playing page.</summary>
    ICommand OpenTrackCommand { get; }
}
