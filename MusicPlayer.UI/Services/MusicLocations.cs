namespace MusicPlayer.UI.Services;

/// <summary>Best-effort default music folder for the current platform.</summary>
public static class MusicLocations
{
    public static string? GetDefaultFolder()
    {
#if WINDOWS
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return Directory.Exists(folder) ? folder : null;
#elif ANDROID
        try
        {
            var music = Android.OS.Environment
                .GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryMusic);

            if (!string.IsNullOrWhiteSpace(music?.AbsolutePath) && Directory.Exists(music.AbsolutePath))
            {
                return music.AbsolutePath;
            }
        }
        catch
        {
            // Storage may not be mounted; the user can pick a folder manually.
        }

        return null;
#else
        return null;
#endif
    }

    public static bool HasAudioFiles(string folder, int maxFilesToCheck = 200)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;

            var checkedFiles = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (Domain.SupportedAudioFormats.IsSupported(file)) return true;

                if (++checkedFiles >= maxFilesToCheck) break;
            }
        }
        catch
        {
            // Permission/IO errors are reported by the scanner itself.
        }

        return false;
    }
}
