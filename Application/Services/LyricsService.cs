using System.Security.Cryptography;
using System.Text;
using Domain.Interfaces;
using Domain.Models;

namespace Application.Services;

/// <summary>
/// Use-case level lyrics access (spec section 8, retrieval priority):
///  1. the version the user explicitly chose for this track (embedded / local file / saved),
///  2. lyrics embedded in the audio file (populated by the metadata scanner),
///  3. a matching local .lrc file next to the audio (including a "lyrics" subfolder and a
///     name-similarity fallback, because files are often named "Title   Artist.lrc"),
///  4. the app's own cache of previously fetched, imported or edited lyrics.
///
/// Online search is deliberately separate: the caller previews the results and only then
/// applies one, which is then written into the cache so it is available offline next time.
/// </summary>
public class LyricsService
{
    private readonly ILyricsSearchProvider _searchProvider;
    private readonly string _cacheDirectory;

    public LyricsService(
        ILyricsSearchProvider searchProvider,
        string cacheDirectory)
    {
        _searchProvider = searchProvider;
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>Returns local lyrics for the track, or null when none are available.</summary>
    public async Task<Lyrics?> FindLocalLyricsAsync(
        Music track,
        CancellationToken cancellationToken = default)
    {
        if (track is null)
        {
            return null;
        }

        // An explicit user choice ("Choose version") always wins while its source exists.
        var preference = ReadPreference(track);
        if (preference is { } preferred)
        {
            var preferredLyrics = await LoadVersionAsync(track, preferred, cancellationToken)
                .ConfigureAwait(false);
            if (HasContent(preferredLyrics))
            {
                return preferredLyrics;
            }

            // The chosen source disappeared (file replaced, access lost): fall through to the
            // automatic chain instead of showing nothing.
        }

        // The scanner stores embedded lyrics on the Music object (see TagLibMetadataReader),
        // so no second full-file read is needed here.
        if (track.Lyrics is { } alreadyLoaded && HasContent(alreadyLoaded))
        {
            return ReparseIfNeeded(alreadyLoaded);
        }

        var sidecar = await Task.Run(() => FindSidecarPath(track), cancellationToken)
            .ConfigureAwait(false);
        var sidecarLyrics = await TryReadVersionAsync(sidecar, LyricsSource.LocalLrcFile, cancellationToken)
            .ConfigureAwait(false);
        if (HasContent(sidecarLyrics))
        {
            return sidecarLyrics;
        }

        return await TryReadVersionAsync(GetCachePath(track), LyricsSource.LrcLib, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Searches the online provider and returns preview candidates only.</summary>
    public Task<IReadOnlyList<LyricsSearchResult>> SearchOnlineAsync(
        Music track,
        CancellationToken cancellationToken = default)
        => _searchProvider.SearchAsync(track, cancellationToken);

    /// <summary>Converts a confirmed search result into the domain lyrics model.</summary>
    public Lyrics CreateFromSearchResult(LyricsSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SyncedLyrics))
        {
            var parsed = LrcParser.Parse(result.SyncedLyrics, LyricsSource.LrcLib);
            if (parsed.Lines.Count > 0)
            {
                return parsed;
            }
        }

        return new Lyrics
        {
            Type = LyricsType.Plain,
            Source = LyricsSource.LrcLib,
            Text = (result.PlainLyrics ?? result.SyncedLyrics ?? string.Empty).Trim(),
        };
    }

    /// <summary>
    /// Lists the versions that exist for this track so the UI can offer a picker. The check
    /// only looks at files and in-memory state; parsing happens when a version is loaded.
    /// </summary>
    public async Task<IReadOnlyList<LyricsVersionInfo>> GetAvailableVersionsAsync(
        Music track,
        CancellationToken cancellationToken = default)
    {
        var versions = new List<LyricsVersionInfo>();

        var hasEmbedded = track.Lyrics is { } embedded && HasContent(embedded);
        versions.Add(new LyricsVersionInfo
        {
            Source = LyricsSource.EmbeddedMetadata,
            Label = hasEmbedded ? "Embedded in the music file" : "Embedded in the music file (none)",
            IsAvailable = hasEmbedded,
        });

        var sidecar = await Task.Run(() => FindSidecarPath(track), cancellationToken)
            .ConfigureAwait(false);
        versions.Add(new LyricsVersionInfo
        {
            Source = LyricsSource.LocalLrcFile,
            Label = sidecar is not null ? $"Local file: {Path.GetFileName(sidecar)}" : "Local .lrc file (none found)",
            IsAvailable = sidecar is not null,
        });

        var cachePath = GetCachePath(track);
        var hasCache = File.Exists(cachePath);
        var preference = ReadPreference(track);
        versions.Add(new LyricsVersionInfo
        {
            Source = preference == LyricsSource.Manual ? LyricsSource.Manual : LyricsSource.LrcLib,
            Label = hasCache
                ? preference == LyricsSource.Manual ? "Edited / imported (saved in app)" : "Saved online copy"
                : "Saved in app (none)",
            IsAvailable = hasCache,
        });

        return versions;
    }

    /// <summary>Loads one specific version, ignoring the priority order. Null when unavailable.</summary>
    public async Task<Lyrics?> LoadVersionAsync(
        Music track,
        LyricsSource source,
        CancellationToken cancellationToken = default)
    {
        switch (source)
        {
            case LyricsSource.EmbeddedMetadata:
                return track.Lyrics is { } embedded && HasContent(embedded)
                    ? ReparseIfNeeded(embedded)
                    : null;

            case LyricsSource.LocalLrcFile:
            {
                var sidecar = await Task.Run(() => FindSidecarPath(track), cancellationToken)
                    .ConfigureAwait(false);
                return await TryReadVersionAsync(sidecar, LyricsSource.LocalLrcFile, cancellationToken)
                    .ConfigureAwait(false);
            }

            case LyricsSource.LrcLib:
            case LyricsSource.Manual:
                return await TryReadVersionAsync(GetCachePath(track), source, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return null;
        }
    }

    /// <summary>Records the user's chosen version so it is preferred on later visits.</summary>
    public Task SetPreferenceAsync(Music track, LyricsSource source, CancellationToken cancellationToken = default)
        => WritePreferenceAsync(track, source, cancellationToken);

    /// <summary>Drops the explicit choice, returning to the automatic priority chain.</summary>
    public void ClearPreference(Music track)
    {
        try
        {
            var path = GetPreferencePath(track);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Saves lyrics text so the next session finds it without network access.</summary>
    public async Task SaveCachedLyricsAsync(
        Music track,
        Lyrics lyrics,
        CancellationToken cancellationToken = default)
    {
        var text = lyrics.Lines.Count > 0 ? LrcParser.ToLrc(lyrics) : lyrics.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = GetCachePath(track);
        await File.WriteAllTextAsync(cachePath, text, cancellationToken)
            .ConfigureAwait(false);

        // Embedded/local lyrics that the user adjusted are now an app-side version; anything
        // else (online result, import, manual edit) keeps its own source.
        var preference = lyrics.Source is LyricsSource.EmbeddedMetadata or LyricsSource.LocalLrcFile
            ? LyricsSource.Manual
            : lyrics.Source;

        await WritePreferenceAsync(track, preference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every cached artifact for a track (used when the file is deleted).</summary>
    public void DeleteCachedLyrics(Music track)
    {
        TryDelete(GetCachePath(track));
        TryDelete(GetPreferencePath(track));
        TryDelete(GetCachePath(track) + ".override"); // legacy marker from older builds
    }

    /// <summary>True when the user pinned a specific version for this track.</summary>
    public bool HasPreference(Music track) => ReadPreference(track) is not null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<Lyrics?> TryReadVersionAsync(
        string? path,
        LyricsSource source,
        CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return null;
        }

        var text = await TryReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var parsed = LrcParser.Parse(text, source);
        return HasContent(parsed) ? parsed : null;
    }

    private static async Task<string?> TryReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task WritePreferenceAsync(Music track, LyricsSource source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);

        try
        {
            await File.WriteAllTextAsync(GetPreferencePath(track), source.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private LyricsSource? ReadPreference(Music track)
    {
        try
        {
            var path = GetPreferencePath(track);
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Enum.TryParse<LyricsSource>(text, out var source))
                {
                    return source;
                }
            }

            // Builds before the version picker stored online overrides in a ".override"
            // marker next to the cache file; keep honoring it after an update.
            if (File.Exists(GetCachePath(track) + ".override"))
            {
                return LyricsSource.LrcLib;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private string GetPreferencePath(Music track) => GetCachePath(track) + ".preference";

    private static string? FindSidecarPath(Music track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(track.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        // 1. Exact same file name: song.mp3 -> song.lrc
        var exact = Path.ChangeExtension(track.FilePath, ".lrc");
        if (File.Exists(exact))
        {
            return exact;
        }

        var upper = Path.ChangeExtension(track.FilePath, ".LRC");
        if (File.Exists(upper))
        {
            return upper;
        }

        // 2. Exact same file name inside a "lyrics" subfolder.
        var lyricsDirectory = Path.Combine(directory, "lyrics");
        var name = Path.GetFileNameWithoutExtension(track.FilePath);
        var inLyricsFolder = Path.Combine(lyricsDirectory, name + ".lrc");
        if (File.Exists(inLyricsFolder))
        {
            return inLyricsFolder;
        }

        // 3. Name-similarity fallback for files like "Title   Artist.lrc" or
        //    "Title (Cover).mp3" next to "Title.lrc". Scoped to this folder on purpose:
        //    scanning the whole library for each lookup would be wasteful.
        var candidates = new List<string>();
        TryAddLrcFiles(directory, candidates);
        if (Directory.Exists(lyricsDirectory))
        {
            TryAddLrcFiles(lyricsDirectory, candidates);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Fuzzy matching is meaningless without a title; exact-file matching above still
        // works in that case.
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var wanted = new[]
            {
                Normalize(track.Name),
                Normalize($"{track.Name} {track.ArtistName}"),
                Normalize($"{track.Name} - {track.ArtistName}"),
            }
            .Where(value => value.Length > 0)
            .Distinct()
            .ToArray();

        if (wanted.Length == 0)
        {
            return null;
        }

        string? bestPath = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var candidateName = Normalize(Path.GetFileNameWithoutExtension(candidate));
            if (candidateName.Length == 0)
            {
                continue;
            }

            var score = 0;
            foreach (var target in wanted)
            {
                if (candidateName == target)
                {
                    score = Math.Max(score, 100);
                }
                else if (target.Length >= 3 &&
                         candidateName.Length >= 3 &&
                         (candidateName.Contains(target, StringComparison.Ordinal) ||
                          target.Contains(candidateName, StringComparison.Ordinal)))
                {
                    score = Math.Max(score, 70);
                }
            }

            var title = Normalize(track.Name);
            var artist = Normalize(track.ArtistName);
            if (title.Length >= 3 &&
                candidateName.Contains(title, StringComparison.Ordinal) &&
                (artist.Length == 0 || candidateName.Contains(artist, StringComparison.Ordinal)))
            {
                score = Math.Max(score, 80);
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPath = candidate;
            }
        }

        return bestScore >= 70 ? bestPath : null;
    }

    private static void TryAddLrcFiles(string directory, List<string> target)
    {
        try
        {
            target.AddRange(Directory.EnumerateFiles(directory, "*.lrc", SearchOption.TopDirectoryOnly));
            target.AddRange(Directory.EnumerateFiles(directory, "*.LRC", SearchOption.TopDirectoryOnly));
        }
        catch
        {
            // A folder without read permission simply contributes no candidates.
        }
    }

    /// <summary>Lowercases and removes punctuation so "Title   Artist" matches "Title Artist".</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) || ch is '(' or ')' or '[' or ']' or '.' or '-' or '_')
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private string GetCachePath(Music track)
    {
        var key = !string.IsNullOrWhiteSpace(track.FilePath)
            ? track.FilePath
            : $"{track.Name}|{track.ArtistName}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(_cacheDirectory, hash + ".lrc");
    }

    private static Lyrics ReparseIfNeeded(Lyrics lyrics)
    {
        if (lyrics.Lines.Count > 0 || string.IsNullOrWhiteSpace(lyrics.Text))
        {
            return lyrics;
        }

        return LrcParser.Parse(lyrics.Text, lyrics.Source);
    }

    private static bool HasContent(Lyrics? lyrics)
        => lyrics is not null &&
           (lyrics.Lines.Count > 0 || !string.IsNullOrWhiteSpace(lyrics.Text));
}
