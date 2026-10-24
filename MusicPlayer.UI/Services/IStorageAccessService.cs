namespace MusicPlayer.UI.Services;

/// <summary>
/// Full-filesystem access (Android "All files access"). Tag editing, file deletion and
/// reading local .lrc files need direct paths; scoped storage blocks all three with only the
/// media-read permission. Windows always has full access.
/// </summary>
public interface IStorageAccessService
{
    /// <summary>True when the app may write/delete files by path right now.</summary>
    bool HasFullFileAccess { get; }

    /// <summary>
    /// Opens the system screen where the user grants full file access. The system shows no
    /// result callback, so callers re-check <see cref="HasFullFileAccess"/> when the user
    /// returns (e.g. from a page OnAppearing or a retry button).
    /// </summary>
    Task RequestFullFileAccessAsync();
}
