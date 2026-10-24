using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Domain.Models;

public class Music
{
    public int Id { get; set; }

    // File information
    public string FilePath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan Duration { get; set; }

    // Basic metadata
    public string Name { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;

    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }

    // Foreign keys
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }

    // Navigation properties
    public Artist? Artist { get; set; }
    public Album? Album { get; set; }
    public List<Genre> Genres { get; set; } = [];
    public Lyrics? Lyrics { get; set; }
    public Artwork? Artwork { get; set; }
    public AudioFileInfo? AudioFileInfo { get; set; }

    // Library information
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAt { get; set; }

    // Derived display helpers (read-only) used by list UI such as TrackItem.
    public string ArtistName => !string.IsNullOrWhiteSpace(Artist?.Name)
        ? Artist.Name
        : !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : "Unknown Artist";

    /// <summary>UI artwork path. Prefers the downscaled thumbnail; falls back to the original.</summary>
    public string? ArtworkPath => Artwork?.ThumbnailPath ?? Artwork?.FilePath;

    /// <summary>Small artwork for list rows (48–60 px). Falls back to the larger thumbnail.</summary>
    public string? ListArtworkPath => Artwork?.SmallThumbnailPath ?? ArtworkPath;

    /// <summary>Original extracted artwork, reserved for the future artwork editor.</summary>
    public string? FullArtworkPath => Artwork?.FilePath;

    public Music() { }
}
