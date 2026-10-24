using Domain.Models;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// One labelled block in the queue sheet. Grouping previously played songs separately from
/// the upcoming list lets the sheet show the whole session instead of pretending the past
/// is still ahead.
/// </summary>
public sealed class QueueSection : List<QueueRow>
{
    public QueueSection(string title, string countLabel, bool isPlayed, IEnumerable<Music> tracks)
        : base(tracks.Select(track => new QueueRow(track, isPlayed)))
    {
        Title = title;
        CountLabel = countLabel;
        IsPlayed = isPlayed;
    }

    public string Title { get; }

    public string CountLabel { get; }

    public bool IsPlayed { get; }
}

/// <summary>One song plus the group it belongs to, so rows can style themselves.</summary>
public sealed class QueueRow
{
    public QueueRow(Music track, bool isPlayed)
    {
        Track = track;
        IsPlayed = isPlayed;
    }

    public Music Track { get; }

    public bool IsPlayed { get; }

    public bool IsUpNext => !IsPlayed;
}
