
namespace Domain.Models;

public enum LyricsType
{
    Plain,
    Synced,
    Enhanced
}

public enum LyricsSource
{
    EmbeddedMetadata,
    LocalLrcFile,
    LrcLib,
    Manual
}

public class Lyrics
{
    public int Id { get; set; }

    public int MusicId { get; set; }

    public LyricsType Type { get; set; }

    public LyricsSource Source { get; set; }

    public string Text { get; set; } = string.Empty;

    public List<LyricsLine> Lines { get; set; } = [];

    public DateTime? LastUpdatedAt { get; set; }
}
