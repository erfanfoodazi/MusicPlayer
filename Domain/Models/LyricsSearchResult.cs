namespace Domain.Models;

/// <summary>
/// One lyrics candidate returned by an online provider (currently LRCLIB). The result is
/// only a preview until the user explicitly applies it.
/// </summary>
public class LyricsSearchResult
{
    /// <summary>Provider that produced this result, e.g. "lrclib".</summary>
    public string Provider { get; set; } = "lrclib";

    /// <summary>Provider-specific track id (LRCLIB uses a numeric id).</summary>
    public long ProviderTrackId { get; set; }

    public string TrackName { get; set; } = string.Empty;

    public string ArtistName { get; set; } = string.Empty;

    public string? AlbumName { get; set; }

    public TimeSpan? Duration { get; set; }

    public bool Instrumental { get; set; }

    /// <summary>Unsynced lyrics as plain text, when the provider has them.</summary>
    public string? PlainLyrics { get; set; }

    /// <summary>Standard or enhanced LRC text, when the provider has it.</summary>
    public string? SyncedLyrics { get; set; }

    public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);

    /// <summary>Human-readable duration for the preview list ("2:43").</summary>
    public string DurationText => Duration is { } d && d > TimeSpan.Zero
        ? $"{(int)d.TotalMinutes}:{d.Seconds:D2}"
        : string.Empty;
}
