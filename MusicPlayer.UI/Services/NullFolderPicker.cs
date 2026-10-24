namespace MusicPlayer.UI.Services;

/// <summary>
/// Temporary Android placeholder so the app compiles and runs while the real Storage Access
/// Framework picker is implemented in the next step. Returning null = "cancelled", which the
/// ViewModel reports honestly instead of pretending folder picking works.
/// </summary>
public class NullFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}
