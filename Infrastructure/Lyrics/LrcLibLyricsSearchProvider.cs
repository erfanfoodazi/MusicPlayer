using System.Globalization;
using System.Text.Json;
using Domain.Interfaces;
using Domain.Models;

namespace Infrastructure.Lyrics;

/// <summary>
/// LRCLIB client (https://lrclib.net). Verified against the current public API:
///   GET /api/search?track_name=...&amp;artist_name=...
/// returns an array of tracks with plainLyrics and syncedLyrics fields.
///
/// JSON is read with <see cref="JsonDocument"/> instead of reflection-based serialization
/// so the trimmed Android Release build keeps working without trimmer annotations.
/// </summary>
public sealed class LrcLibLyricsSearchProvider : ILyricsSearchProvider
{
    private const string BaseAddress = "https://lrclib.net/";
    private const int MaxResults = 10;

    private readonly HttpClient _httpClient;

    public LrcLibLyricsSearchProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseAddress);
        }

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Auralis/1.0 (personal music player)");
        }
    }

    public async Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(
        Music track,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return [];
        }

        var title = NormalizeTitle(track.Name);
        var artist = PrimaryArtist(track.ArtistName);
        var candidates = new List<LyricsSearchResult>();
        var successfulQueries = 0;
        Exception? lastError = null;

        foreach (var relativeUrl in BuildQueries(title, artist, track.ArtistName))
        {
            try
            {
                var results = await GetResultsAsync(relativeUrl, cancellationToken)
                    .ConfigureAwait(false);

                successfulQueries++;
                MergeResults(candidates, results);

                if (HasStrongMatch(candidates, title, artist))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                // A single LRCLIB query can be temporarily unavailable. Keep the other
                // candidates instead of failing the whole search.
                lastError = ex;
            }
        }

        if (successfulQueries == 0 && lastError is not null)
        {
            throw new InvalidOperationException("LRCLIB search failed.", lastError);
        }

        return candidates
            .OrderBy(result => TitleRank(result.TrackName, title))
            .ThenBy(result => ArtistRank(result.ArtistName, artist, track.ArtistName))
            .ThenBy(result => DurationDistance(result, track))
            .ThenByDescending(result => result.HasSyncedLyrics)
            .Take(MaxResults)
            .ToList();
    }

    /// <summary>
    /// Builds progressively broader queries. Many files tag every featured artist in the
    /// artist field (for example "Dorcci, Mohammad Rashidian…"); sending that full string
    /// to the exact-field endpoint returns nothing. The primary artist is enough for the
    /// flexible query, and the title-only query is the final safety net.
    /// </summary>
    private static IEnumerable<string> BuildQueries(
        string title,
        string primaryArtist,
        string fullArtist)
    {
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            yield return "api/search?q=" +
                Uri.EscapeDataString($"{title} {primaryArtist}");
        }

        yield return "api/search?track_name=" + Uri.EscapeDataString(title);

        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            yield return "api/search?q=" + Uri.EscapeDataString(title);
        }
        else if (!string.Equals(primaryArtist, fullArtist.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // Some releases only match when the complete, multi-artist tag is searched.
            yield return "api/search?q=" +
                Uri.EscapeDataString($"{title} {fullArtist.Trim()}");
        }
    }

    private async Task<List<LyricsSearchResult>> GetResultsAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        // LRCLIB occasionally returns a transient 503. One quiet retry makes search
        // reliable without showing an error for a request that immediately succeeds.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = await _httpClient.GetStringAsync(relativeUrl, cancellationToken)
                    .ConfigureAwait(false);
                return ParseResults(json);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await Task.Delay(220, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void MergeResults(
        List<LyricsSearchResult> target,
        IEnumerable<LyricsSearchResult> incoming)
    {
        foreach (var result in incoming)
        {
            // Instrumental entries have no text to display or save.
            if (string.IsNullOrWhiteSpace(result.PlainLyrics) &&
                string.IsNullOrWhiteSpace(result.SyncedLyrics))
            {
                continue;
            }

            var existingIndex = target.FindIndex(existing => IsSameResult(existing, result));
            if (existingIndex < 0)
            {
                target.Add(result);
            }
            else if (result.HasSyncedLyrics && !target[existingIndex].HasSyncedLyrics)
            {
                target[existingIndex] = result;
            }
        }
    }

    private static bool IsSameResult(LyricsSearchResult left, LyricsSearchResult right)
    {
        if (left.ProviderTrackId != 0 && right.ProviderTrackId != 0)
        {
            return left.ProviderTrackId == right.ProviderTrackId;
        }

        return string.Equals(left.TrackName, right.TrackName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ArtistName, right.ArtistName, StringComparison.OrdinalIgnoreCase) &&
               left.Duration == right.Duration;
    }

    private static bool HasStrongMatch(
        IReadOnlyList<LyricsSearchResult> results,
        string title,
        string primaryArtist)
        => results.Any(result =>
            result.HasSyncedLyrics &&
            TitleRank(result.TrackName, title) == 0 &&
            (string.IsNullOrWhiteSpace(primaryArtist) ||
             result.ArtistName.Contains(primaryArtist, StringComparison.OrdinalIgnoreCase)));

    private static string NormalizeTitle(string title)
    {
        var normalized = string.Join(
            ' ',
            title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Ripped files often carry a qualifier that LRCLIB does not include in the title.
        foreach (var qualifier in new[]
                 {
                     "(official audio)", "(official video)", "(official music video)",
                     "(lyrics)", "(lyric video)", "(audio)", "(visualizer)", "[official audio]",
                     "[official video]", "[lyrics]", "[lyric video]"
                 })
        {
            if (normalized.EndsWith(qualifier, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^qualifier.Length].Trim();
                break;
            }
        }

        return normalized.Length > 0 ? normalized : title.Trim();
    }

    private static string PrimaryArtist(string artistName)
    {
        var artist = artistName.Trim();
        if (artist.Length == 0)
        {
            return string.Empty;
        }

        foreach (var separator in new[] { ",", ";", " feat.", " feat ", " ft.", " ft ", " featuring " })
        {
            var index = artist.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                artist = artist[..index].Trim();
            }
        }

        return artist;
    }

    private static int TitleRank(string resultTitle, string requestedTitle)
    {
        if (string.Equals(resultTitle.Trim(), requestedTitle, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return resultTitle.Contains(requestedTitle, StringComparison.OrdinalIgnoreCase) ||
               requestedTitle.Contains(resultTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
    }

    private static int ArtistRank(
        string resultArtist,
        string primaryArtist,
        string fullArtist)
    {
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            return 0;
        }

        if (resultArtist.Contains(primaryArtist, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return resultArtist.Contains(fullArtist.Trim(), StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
    }

    private static List<LyricsSearchResult> ParseResults(string json)
    {
        var results = new List<LyricsSearchResult>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var durationSeconds = GetDouble(element, "duration");
            results.Add(new LyricsSearchResult
            {
                Provider = "lrclib",
                ProviderTrackId = GetLong(element, "id"),
                TrackName = GetString(element, "trackName") ?? string.Empty,
                ArtistName = GetString(element, "artistName") ?? string.Empty,
                AlbumName = GetString(element, "albumName"),
                Duration = durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null,
                Instrumental = GetBool(element, "instrumental"),
                PlainLyrics = GetString(element, "plainLyrics"),
                SyncedLyrics = GetString(element, "syncedLyrics"),
            });
        }

        return results;
    }

    private static TimeSpan DurationDistance(LyricsSearchResult result, Music track)
    {
        if (result.Duration is not { } resultDuration || track.Duration <= TimeSpan.Zero)
        {
            return TimeSpan.FromDays(1);
        }

        return (resultDuration - track.Duration).Duration();
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : 0;

    private static double? GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool GetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.True;
}
