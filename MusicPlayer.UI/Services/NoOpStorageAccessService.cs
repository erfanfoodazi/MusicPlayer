namespace MusicPlayer.UI.Services;

/// <summary>Used on platforms without scoped storage (Windows): access is always granted.</summary>
public sealed class NoOpStorageAccessService : IStorageAccessService
{
    public bool HasFullFileAccess => true;

    public Task RequestFullFileAccessAsync() => Task.CompletedTask;
}
