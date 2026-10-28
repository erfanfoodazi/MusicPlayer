namespace MusicPlayer.UI.Services.Playback;

/// <summary>
/// Small cross-platform contract for one audio engine. Implementations own a single
/// loaded track at a time; queue/history/repeat logic stays in <see cref="PlayerService"/>.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Current playback position, in seconds.</summary>
    double PositionSeconds { get; }

    /// <summary>Duration of the loaded media, in seconds, or 0 while unknown.</summary>
    double DurationSeconds { get; }

    /// <summary>Raised when the current track reaches its end.</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// Raised when the engine moved into the track primed with <see cref="SetNextTrack"/>
    /// without a silent gap. No <see cref="PlaybackEnded"/> is raised for that transition.
    /// </summary>
    event EventHandler<GaplessTransitionEventArgs>? GaplessTrackStarted;

    /// <summary>Raised when the platform engine cannot load or play the file.</summary>
    event EventHandler<AudioPlayerFailureEventArgs>? Failed;

    /// <summary>
    /// Raised when the active output peripheral (headset, headphones, Bluetooth band or
    /// speaker, USB DAC) disappears while the app owns the audio. Playback pauses so the
    /// song does not suddenly blast out of the phone speaker.
    /// </summary>
    event EventHandler? AudioDeviceDisconnected;

    /// <summary>
    /// Raised when Android hands audio focus to another app. The engine has already paused;
    /// the owner only has to mirror that state.
    /// </summary>
    event EventHandler? AudioFocusLost;

    /// <summary>
    /// Raised when a transient focus loss ends, after the engine has resumed by itself.
    /// </summary>
    event EventHandler? AudioFocusGained;

    /// <summary>
    /// Loads a local file. When <paramref name="autoPlay"/> is true the engine starts
    /// playback as soon as the media is ready.
    /// </summary>
    /// <returns>The media duration reported by the platform decoder.</returns>
    Task<TimeSpan> LoadAsync(string filePath, bool autoPlay, CancellationToken cancellationToken = default);

    Task PlayAsync();

    Task PauseAsync();

    Task StopAsync();

    Task SeekAsync(double seconds);

    /// <summary>
    /// Primes the engine with the file that should follow the current one at the end of the
    /// track, so the decoder can cross over without silence. Pass null to clear the priming.
    /// Platforms without sample-accurate crossfade support ignore this.
    /// </summary>
    void SetNextTrack(string? filePath) { }

    /// <summary>Sets output volume from 0 to 1.</summary>
    void SetVolume(double volume);

    /// <summary>True when the engine can change tempo without restarting playback.</summary>
    bool SupportsPlaybackRate => false;

    /// <summary>True when pitch can be adjusted independently (Android MediaPlayer).</summary>
    bool SupportsPitch => false;

    /// <summary>True when a platform audio effect equalizer can be attached.</summary>
    bool SupportsEqualizer => false;

    /// <summary>Names reported by the platform equalizer; empty when unsupported.</summary>
    IReadOnlyList<string> EqualizerPresets => [];

    /// <summary>Sets playback speed, where 1.0 is normal. No-op when unsupported.</summary>
    void SetPlaybackRate(double rate) { }

    /// <summary>Sets the pitch factor, where 1.0 is normal. No-op when unsupported.</summary>
    void SetPitch(double pitch) { }

    /// <summary>Enables or bypasses the platform equalizer.</summary>
    void SetEqualizerEnabled(bool enabled) { }

    /// <summary>Applies one of <see cref="EqualizerPresets"/> by name.</summary>
    void SetEqualizerPreset(string preset) { }
}

public sealed class AudioPlayerFailureEventArgs : EventArgs
{
    public AudioPlayerFailureEventArgs(Exception exception) => Exception = exception;

    public Exception Exception { get; }
}

public sealed class GaplessTransitionEventArgs : EventArgs
{
    public GaplessTransitionEventArgs(string filePath) => FilePath = filePath;

    /// <summary>File the engine was primed with when the transition happened.</summary>
    public string FilePath { get; }
}
