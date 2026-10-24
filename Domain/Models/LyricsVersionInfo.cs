namespace Domain.Models;

/// <summary>One selectable lyrics version for a track, shown in the "Choose version" picker.</summary>
public class LyricsVersionInfo
{
    public LyricsSource Source { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>False when the source exists in the list but currently has no content.</summary>
    public bool IsAvailable { get; init; }
}
