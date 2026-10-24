namespace Domain.Models;

/// <summary>
/// The user-editable tag values for one audio file. <c>null</c> means "do not touch this
/// tag", so an editor can leave fields it does not show alone.
/// </summary>
public class MusicTagEdit
{
    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public int? Year { get; set; }

    public int? TrackNumber { get; set; }

    public int? DiscNumber { get; set; }

    public List<string>? Genres { get; set; }

    /// <summary>Plain or LRC text for the embedded lyrics tag. Null leaves the tag untouched.</summary>
    public string? LyricsText { get; set; }

    /// <summary>New cover image bytes. Null leaves the current cover untouched.</summary>
    public byte[]? CoverImageBytes { get; set; }

    public string? CoverMimeType { get; set; }

    /// <summary>Removes the embedded cover when true (ignored when new bytes are supplied).</summary>
    public bool RemoveCover { get; set; }
}
