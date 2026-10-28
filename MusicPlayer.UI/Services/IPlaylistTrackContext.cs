using System.Windows.Input;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Implemented by a page view model whose list is a playlist, so the shared track menu can
/// offer "Remove from playlist" only where it makes sense.
/// </summary>
public interface IPlaylistTrackContext
{
    ICommand RemoveTrackCommand { get; }
}
