namespace MusicPlayer.UI.Services;

/// <summary>
/// Lets the user choose a music folder. Folder picking is platform-specific (Windows:
/// FolderPicker; Android: Storage Access Framework), so the UI depends on this interface
/// and each platform supplies its implementation (spec section 2).
/// </summary>
public interface IFolderPicker
{
    /// <summary>
    /// Shows the platform folder picker and returns the chosen location - a filesystem path
    /// on Windows, a content-tree URI on Android - or null when the user cancels or the
    /// platform has no implementation yet.
    /// </summary>
    Task<string?> PickFolderAsync();
}
