#if ANDROID
using Android.Provider;
using Domain;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Uses Android's MediaStore audio index. Scoped storage hides arbitrary folders from a
/// normal directory walk, and the public Music directory is not where every download or
/// messaging app saves audio. MediaStore gives the player the same complete list Samsung
/// Music, Oto Music and other players use.
/// </summary>
public sealed class AndroidMusicFileDiscovery : IMusicFileDiscovery
{
    private const string DataColumn = "_data";
    private const string IsMusicColumn = "is_music";

    public bool ScansWholeLibrary => true;

    public string SourcePath
    {
        get
        {
            try
            {
                return Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
                       ?? "/storage/emulated/0";
            }
            catch
            {
                return "/storage/emulated/0";
            }
        }
    }

    public string SourceLabel => "all device music";

    public Task<IReadOnlyList<string>> DiscoverAsync(
        string? preferredFolder,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(
            () => QueryMediaStore(cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<string> QueryMediaStore(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var resolver = Android.App.Application.Context.ContentResolver;
            if (resolver is null)
            {
                return [];
            }

            var projection = new[] { DataColumn };
            var selection = $"{IsMusicColumn} != 0";
            var mediaUri = MediaStore.Audio.Media.ExternalContentUri;
            if (mediaUri is null)
            {
                return [];
            }

            using var cursor = resolver.Query(
                mediaUri,
                projection,
                selection,
                null,
                null);

            if (cursor is null)
            {
                return [];
            }

            var dataIndex = cursor.GetColumnIndex(DataColumn);
            if (dataIndex < 0)
            {
                return [];
            }

            while (cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = cursor.GetString(dataIndex);
                if (string.IsNullOrWhiteSpace(path) || !SupportedAudioFormats.IsSupported(path))
                {
                    continue;
                }

                // MediaStore can retain rows after a file was moved or deleted. The player
                // needs a real path for TagLib artwork and local .lrc lookup.
                if (File.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Permission problems or a vendor MediaStore quirk must not abort startup. The
            // caller falls back to scanning the saved folder.
            return [];
        }

        return paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
#endif
