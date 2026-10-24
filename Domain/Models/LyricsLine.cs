
namespace Domain.Models;

public class LyricsLine
{
    public int Id { get; set; }

    public int LyricsId { get; set; }

    public int Index { get; set; }

    public string Text { get; set; } = string.Empty;

    // Time from the beginning of the song
    public TimeSpan StartTime { get; set; }

    public TimeSpan? EndTime { get; set; }

    public List<LyricWord> Words { get; set; } = [];
}
