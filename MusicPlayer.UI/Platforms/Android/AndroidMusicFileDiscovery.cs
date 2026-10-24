#if ANDROID
using Android.Content;
using Android.Database;
using Android.Provider;
using Domain;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Discovers music through Android's MediaStore, following the same rules the system media
/// scanner and other music players use:
///  - Audio rows flagged as ringtones, notifications, alarms, podcasts, audiobooks or
///    recordings are not music and are skipped.
///  - Voice notes and recorder folders are skipped by path.
///  - Files without Android's music flag are still included when they are in a Music folder,
///    or when they are long enough to be a song (downloads and messaging apps often never
///    get the flag). Short clips and voice messages are excluded.
/// Both the audio collection and the audio-MIME rows in the files collection are queried on
/// every external volume, so an SD card is not missed.
/// </summary>
public sealed class AndroidMusicFileDiscovery : IMusicFileDiscovery
{
    private const string DataColumn = "_data";
    private const string DurationColumn = "duration";
    private const string MediaTypeColumn = "media_type";
    private const string MimeTypeColumn = "mime_type";
    private const int MediaTypeAudio = 2;
    private const string IsMusicColumn = "is_music";
    private const string IsRingtoneColumn = "is_ringtone";
    private const string IsNotificationColumn = "is_notification";
    private const string IsAlarmColumn = "is_alarm";
    private const string IsPodcastColumn = "is_podcast";
    private const string IsAudiobookColumn = "is_audiobook";
    private const string IsRecordingColumn = "is_recording";

    /// <summary>
    /// Android itself considers a file music only when it is at least 30 seconds long.
    /// Applying the same floor to untagged audio keeps voice messages and sound effects out
    /// without hiding regular songs.
    /// </summary>
    private static readonly TimeSpan MinimumUntaggedDuration = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.Ordinal)
    {
        "ringtones",
        "notifications",
        "alarms",
        "ui",
        "system",
        "recordings",
        "callrecordings",
        "voicerecordings",
        "audiorecordings",
        "voicerecorder",
        "voicenotes",
        "voicemail",
        "whatsappvoicenotes",
        "whatsappaudio",
    };

    private static readonly string[] VoiceNotePrefixes = ["AUD-", "PTT-"];

    public bool ScansWholeLibrary => true;

    public bool HasLibraryAccess => AndroidMediaAccess.HasReadAccess;

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

    public string SourceLabel => "device music";

    public Task<IReadOnlyList<string>> DiscoverAsync(
        string? preferredFolder,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(
            async () =>
            {
                // The startup permission dialog may still be open when the library loads.
                // Wait for its result instead of caching an empty library from a transient
                // permission denial.
                if (!AndroidMediaAccess.HasReadAccess)
                {
                    await AndroidMediaAccess.WaitForStartupRequestAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!AndroidMediaAccess.HasReadAccess || cancellationToken.IsCancellationRequested)
                {
                    return [];
                }

                return QueryMediaStore(cancellationToken);
            },
            cancellationToken);
    }

    private static IReadOnlyList<string> QueryMediaStore(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var resolver = Android.App.Application.Context.ContentResolver;
        if (resolver is null)
        {
            return [];
        }

        foreach (var volume in GetExternalVolumes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Query each table independently: a vendor MediaStore problem in one must not
            // hide the files found by the other.
            TryQuery(() => QueryAudioTable(resolver, volume, paths, cancellationToken));
            TryQuery(() => QueryFilesByAudioMimeType(resolver, volume, paths, cancellationToken));
        }

        return paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> GetExternalVolumes()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return ["external"];
        }

        try
        {
            var volumes = MediaStore.GetExternalVolumeNames(Android.App.Application.Context);
            if (volumes is { Count: > 0 })
            {
                return [.. volumes];
            }
        }
        catch
        {
            // Fall back to the synthetic volume used before scoped storage.
        }

        return ["external"];
    }

    private static void TryQuery(Action query)
    {
        try
        {
            query();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A revoked permission or vendor quirk must not abort the scan. Other volumes
            // and the folder fallback still run.
        }
    }

    private static void QueryAudioTable(
        ContentResolver resolver,
        string volume,
        HashSet<string> paths,
        CancellationToken cancellationToken)
    {
        var mediaUri = MediaStore.Audio.Media.GetContentUri(volume);
        if (mediaUri is null)
        {
            return;
        }

        var projection = new List<string>
        {
            DataColumn,
            DurationColumn,
            IsMusicColumn,
            IsRingtoneColumn,
            IsNotificationColumn,
            IsAlarmColumn,
            IsPodcastColumn,
        };

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            projection.Add(IsAudiobookColumn);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            projection.Add(IsRecordingColumn);
        }

        using var cursor = resolver.Query(mediaUri, [.. projection], null, null, null);
        if (cursor is null)
        {
            return;
        }

        var dataIndex = cursor.GetColumnIndex(DataColumn);
        if (dataIndex < 0)
        {
            return;
        }

        var durationIndex = cursor.GetColumnIndex(DurationColumn);
        var isMusicIndex = cursor.GetColumnIndex(IsMusicColumn);
        var isRingtoneIndex = cursor.GetColumnIndex(IsRingtoneColumn);
        var isNotificationIndex = cursor.GetColumnIndex(IsNotificationColumn);
        var isAlarmIndex = cursor.GetColumnIndex(IsAlarmColumn);
        var isPodcastIndex = cursor.GetColumnIndex(IsPodcastColumn);
        var isAudiobookIndex = cursor.GetColumnIndex(IsAudiobookColumn);
        var isRecordingIndex = cursor.GetColumnIndex(IsRecordingColumn);

        while (cursor.MoveToNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = cursor.GetString(dataIndex);
            if (!IsUsablePath(path))
            {
                continue;
            }

            var isCategoryAudio =
                ReadFlag(cursor, isRingtoneIndex) ||
                ReadFlag(cursor, isNotificationIndex) ||
                ReadFlag(cursor, isAlarmIndex) ||
                ReadFlag(cursor, isPodcastIndex) ||
                ReadFlag(cursor, isAudiobookIndex) ||
                ReadFlag(cursor, isRecordingIndex);

            if (isCategoryAudio || IsNonMusicPath(path!))
            {
                continue;
            }

            var isMusic = ReadFlag(cursor, isMusicIndex);
            var duration = ReadDuration(cursor, durationIndex);

            if (isMusic || IsLikelySong(path!, duration))
            {
                paths.Add(path!);
            }
        }
    }

    /// <summary>
    /// Catches audio rows that are missing from the audio collection on some vendor builds.
    /// The media-type filter is important: app and game packages store thousands of sound
    /// effects with an audio MIME type, but those rows are MEDIA_TYPE_NONE, not audio media.
    /// </summary>
    private static void QueryFilesByAudioMimeType(
        ContentResolver resolver,
        string volume,
        HashSet<string> paths,
        CancellationToken cancellationToken)
    {
        var mediaUri = MediaStore.Files.GetContentUri(volume);
        if (mediaUri is null)
        {
            return;
        }

        var projection = new List<string> { DataColumn, MimeTypeColumn };
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            projection.Add(DurationColumn);
        }

        using var cursor = resolver.Query(
            mediaUri,
            [.. projection],
            $"{MimeTypeColumn} LIKE ? AND {MediaTypeColumn} = ?",
            ["audio/%", MediaTypeAudio.ToString()],
            null);

        if (cursor is null)
        {
            return;
        }

        var dataIndex = cursor.GetColumnIndex(DataColumn);
        if (dataIndex < 0)
        {
            return;
        }

        var durationIndex = cursor.GetColumnIndex(DurationColumn);

        while (cursor.MoveToNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = cursor.GetString(dataIndex);
            if (!IsUsablePath(path) || IsNonMusicPath(path!))
            {
                continue;
            }

            if (IsLikelySong(path!, ReadDuration(cursor, durationIndex)))
            {
                paths.Add(path!);
            }
        }
    }

    private static bool IsUsablePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && SupportedAudioFormats.IsSupported(path)
           && File.Exists(path);

    /// <summary>
    /// Rejects system sound folders, voice notes and recordings regardless of how the
    /// device flagged them. The path is used because MediaStore category flags are not set
    /// consistently by messaging apps and some OEM media scanners.
    /// </summary>
    private static bool IsNonMusicPath(string path)
    {
        if (VoiceNotePrefixes.Any(prefix =>
                Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = new string(segment.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (normalized.Length > 0 && ExcludedFolders.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelySong(string path, long durationMilliseconds)
    {
        if (IsInMusicFolder(path))
        {
            return true;
        }

        return durationMilliseconds >= MinimumUntaggedDuration.TotalMilliseconds;
    }

    private static bool IsInMusicFolder(string path)
    {
        // Case-sensitive on purpose: the standard public folder is "Music". Lowercase
        // "music" folders are common inside unpacked games and app asset bundles.
        return path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "Music", StringComparison.Ordinal));
    }

    private static long ReadDuration(ICursor cursor, int index)
        => index >= 0 && !cursor.IsNull(index) ? cursor.GetLong(index) : 0;

    private static bool ReadFlag(ICursor cursor, int index)
        => index >= 0 && !cursor.IsNull(index) && cursor.GetInt(index) != 0;
}
#endif