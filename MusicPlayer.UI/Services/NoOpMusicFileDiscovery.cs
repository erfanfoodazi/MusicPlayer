namespace MusicPlayer.UI.Services;

/// <summary>Desktop fallback: the normal folder scanner supplies the file list.</summary>
public sealed class NoOpMusicFileDiscovery : IMusicFileDiscovery
{
    public bool ScansWholeLibrary => false;

    public bool HasLibraryAccess => true;

    public string SourcePath => string.Empty;

    public string SourceLabel => "music folder";

    public Task<IReadOnlyList<string>> DiscoverAsync(string? preferredFolder, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
