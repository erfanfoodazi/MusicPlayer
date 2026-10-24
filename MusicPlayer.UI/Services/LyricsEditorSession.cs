using Domain.Models;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Carries lyrics text into the editor page (created by Shell without constructor
/// arguments) and hands the edited result back to the lyrics page.
/// </summary>
public sealed class LyricsEditorSession
{
    public Music? Track { get; set; }

    public string InitialText { get; set; } = string.Empty;

    /// <summary>Set by the editor when the user saved; consumed by the lyrics page.</summary>
    public Lyrics? Result { get; set; }
}
