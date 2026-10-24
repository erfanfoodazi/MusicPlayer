namespace MusicPlayer.UI.Services.Playback;

/// <summary>
/// Safe fallback for platforms without an engine. It keeps the UI responsive but makes it
/// obvious that playback is unsupported instead of pretending a timer is audio.
/// </summary>
#pragma warning disable CS0067 // The fallback never raises engine events.
public sealed class NullAudioPlayer : IAudioPlayer
{
    public double PositionSeconds => 0;
    public double DurationSeconds => 0;

    public event EventHandler? PlaybackEnded;
    public event EventHandler<AudioPlayerFailureEventArgs>? Failed;
    public event EventHandler? AudioDeviceDisconnected;

    public Task<TimeSpan> LoadAsync(
        string filePath,
        bool autoPlay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new PlatformNotSupportedException(
            "Audio playback is only implemented for Android and Windows in this build.");
    }

    public Task PlayAsync() => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task SeekAsync(double seconds) => Task.CompletedTask;
    public void SetVolume(double volume) { }
    public void Dispose() { }
}
#pragma warning restore CS0067
