using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Android does not allow apps to scan arbitrary folders anymore (scoped storage). This picker
/// returns the public Music folder, which is the standard location for user audio. Media
/// permission is requested by MainActivity when the app starts.
/// </summary>
public sealed class AndroidFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync()
    {
        var candidates = new List<string>();

        var music = Android.OS.Environment
            .GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic)?.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(music)) candidates.Add(music);

        var download = Android.OS.Environment
            .GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(download)) candidates.Add(download);

        // Prefer the folder that actually contains audio, so a first launch finds music.
        foreach (var folder in candidates)
        {
            if (MusicLocations.HasAudioFiles(folder))
            {
                return Task.FromResult<string?>(folder);
            }
        }

        var firstExisting = candidates.FirstOrDefault(Directory.Exists);
        return Task.FromResult(firstExisting);
    }
}
