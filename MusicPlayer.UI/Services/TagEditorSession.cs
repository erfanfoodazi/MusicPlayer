using Domain.Models;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Carries the track being edited from a track row into the tag editor page, which is
/// created by Shell without constructor arguments (like the other routed pages).
/// </summary>
public sealed class TagEditorSession
{
    public Music? Track { get; set; }
}
