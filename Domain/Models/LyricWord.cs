namespace Domain.Models;

/// <summary>
/// One timed word inside an enhanced LRC line (&lt;mm:ss.xx&gt;Hello &lt;mm:ss.xx&gt;world).
/// Standard LRC lines have no words; enhanced lines keep both the full line text and the
/// per-word timings so the UI can highlight word by word.
/// </summary>
public class LyricWord
{
    public int Id { get; set; }

    public int LyricsLineId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Time from the beginning of the song (after any offset adjustment).</summary>
    public TimeSpan StartTime { get; set; }
}
