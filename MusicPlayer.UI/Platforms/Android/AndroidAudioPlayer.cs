#if ANDROID
using Android.Content;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;

namespace MusicPlayer.UI.Services.Playback;

/// <summary>
/// Android implementation backed by <see cref="MediaPlayer"/>. Formats are limited to what
/// the device's platform decoder supports (MP3, AAC/M4A, FLAC, WAV, OGG/Opus on newer
/// Android versions). Playback params and the platform equalizer are attached to the same
/// audio session so background controls and in-app tools stay in sync.
/// </summary>
public sealed class AndroidAudioPlayer : IAudioPlayer
{
    private MediaPlayer? _player;
    private MediaPlayer? _nextPlayer;
    private string? _nextPath;
    private bool _gaplessPromotionPending;
    private Equalizer? _equalizer;
    private readonly List<string> _equalizerPresets = [];
    private AudioManager? _audioManager;
    private BecomingNoisyReceiver? _noisyReceiver;
    private OutputDeviceCallback? _deviceCallback;
    private FocusChangeListener? _focusListener;
    private bool _hasAudioFocus;
    private bool _pausedForFocus;
    private bool _duckedForFocus;
    private double _volume = 1;
    private bool _disconnectMonitoring;
    private bool _disposed;
    private bool _equalizerEnabled;
    private string _equalizerPreset = string.Empty;
    private double _playbackRate = 1;
    private double _pitch = 1;

    public event EventHandler? AudioDeviceDisconnected;
    public event EventHandler? AudioFocusLost;
    public event EventHandler? AudioFocusGained;

    public AndroidAudioPlayer()
    {
        StartDisconnectMonitoring();
    }

    public bool SupportsPlaybackRate => OperatingSystem.IsAndroidVersionAtLeast(23);

    public bool SupportsPitch => OperatingSystem.IsAndroidVersionAtLeast(23);

    public bool SupportsEqualizer => true;

    public IReadOnlyList<string> EqualizerPresets => _equalizerPresets;

    public void SetPlaybackRate(double rate)
    {
        _playbackRate = Math.Clamp(rate, 0.25, 4);
        MainThread.BeginInvokeOnMainThread(ApplyPlaybackParams);
    }

    public void SetPitch(double pitch)
    {
        _pitch = Math.Clamp(pitch, 0.5, 2);
        MainThread.BeginInvokeOnMainThread(ApplyPlaybackParams);
    }

    public void SetEqualizerEnabled(bool enabled)
    {
        _equalizerEnabled = enabled;
        MainThread.BeginInvokeOnMainThread(ApplyEqualizerState);
    }

    public void SetEqualizerPreset(string preset)
    {
        _equalizerPreset = preset ?? string.Empty;
        MainThread.BeginInvokeOnMainThread(ApplyEqualizerState);
    }

    public double PositionSeconds
    {
        get
        {
            CheckGaplessTransition();

            try
            {
                return (_player?.CurrentPosition ?? 0) / 1000.0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public double DurationSeconds
    {
        get
        {
            CheckGaplessTransition();

            try
            {
                return (_player?.Duration ?? 0) / 1000.0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public event EventHandler? PlaybackEnded;
    public event EventHandler<GaplessTransitionEventArgs>? GaplessTrackStarted;
    public event EventHandler<AudioPlayerFailureEventArgs>? Failed;

    public async Task<TimeSpan> LoadAsync(
        string filePath,
        bool autoPlay,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The audio file no longer exists.", filePath);
        }

        var completion = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        MediaPlayer? createdPlayer = null;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ReleasePlayer();

            var player = new MediaPlayer();
            createdPlayer = player;

            try
            {
                player.SetAudioAttributes(CreateAudioAttributes());

                player.Prepared += (_, _) =>
                {
                    var duration = TimeSpan.FromMilliseconds(Math.Max(0, player.Duration));
                    completion.TrySetResult(duration);

                    EnsureEqualizer(player);
                    ApplyPlaybackParams();
                    ApplyEqualizerState();

                    if (autoPlay)
                    {
                        try
                        {
                            if (TryRequestAudioFocus())
                            {
                                player.Start();
                                ApplyPlaybackParams();
                            }
                            else
                            {
                                // Another app (call, voice assistant) currently owns audio.
                                AudioFocusLost?.Invoke(this, EventArgs.Empty);
                            }
                        }
                        catch (Exception ex)
                        {
                            completion.TrySetException(ex);
                            Failed?.Invoke(this, new AudioPlayerFailureEventArgs(ex));
                        }
                    }
                };

                player.Completion += (_, _) =>
                {
                    // With a gapless successor primed the platform switches players itself;
                    // reporting the end here would make PlayerService reload the track.
                    if (ReferenceEquals(_player, player) && _nextPlayer is null)
                    {
                        PlaybackEnded?.Invoke(this, EventArgs.Empty);
                    }
                };

                player.Error += (_, args) =>
                {
                    args.Handled = true;
                    var exception = new InvalidOperationException(
                        $"Android's media decoder failed (what={args.What}, extra={args.Extra}).");

                    completion.TrySetException(exception);
                    Failed?.Invoke(this, new AudioPlayerFailureEventArgs(exception));
                };

                player.SetDataSource(filePath);
                _player = player;
                player.PrepareAsync();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                Failed?.Invoke(this, new AudioPlayerFailureEventArgs(ex));
            }
        });

        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);

            if (createdPlayer is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (ReferenceEquals(_player, createdPlayer))
                {
                    ReleasePlayer();
                }
            });
        });

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task PlayAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CheckGaplessTransition();
            var player = _player ?? throw new InvalidOperationException("No audio is loaded.");

            if (!TryRequestAudioFocus())
            {
                AudioFocusLost?.Invoke(this, EventArgs.Empty);
                return;
            }

            player.Start();
            ApplyPlaybackParams();
        });

    public Task PauseAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_disposed)
            {
                AbandonAudioFocus();
                CheckGaplessTransition();
                _player?.Pause();
            }
        });

    public Task StopAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_disposed)
            {
                AbandonAudioFocus();
                ReleasePlayer();
            }
        });

    public Task SeekAsync(double seconds) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            CheckGaplessTransition();

            var player = _player;
            if (player is not null)
            {
                player.SeekTo((int)Math.Clamp(seconds * 1000.0, 0, int.MaxValue));
            }
        });

    public void SetVolume(double volume)
    {
        if (_disposed)
        {
            return;
        }

        _volume = Math.Clamp(volume, 0, 1);
        RestoreVolume();
    }

    /// <summary>
    /// Primes a second prepared player. Android starts it at the exact end of the current
    /// track, which removes the decoder restart (the silent gap) between songs that flow
    /// into each other.
    /// </summary>
    public void SetNextTrack(string? filePath)
    {
        if (_disposed)
        {
            return;
        }

        if (MainThread.IsMainThread)
        {
            PrepareNextPlayer(filePath);
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => PrepareNextPlayer(filePath));
        }
    }

    private void PrepareNextPlayer(string? filePath)
    {
        if (_disposed)
        {
            return;
        }

        if (_player is null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            ClearNextPlayer();
            return;
        }

        if (_nextPlayer is not null && string.Equals(_nextPath, filePath, StringComparison.Ordinal))
        {
            return;
        }

        // A successor that is already playing means the crossover started before this call.
        // Releasing it here would cut the song; the poll adopts it and PlayerService arms the
        // new successor right after.
        if (_nextPlayer is { } playing && IsPlayingSafe(playing))
        {
            return;
        }

        ClearNextPlayer();

        try
        {
            var next = new MediaPlayer();
            next.SetAudioAttributes(CreateAudioAttributes());

            next.Prepared += (_, _) =>
            {
                if (!ReferenceEquals(_nextPlayer, next))
                {
                    return;
                }

                try
                {
                    // Never apply PlaybackParams here: setting them on a prepared player starts
                    // it, and SetNextMediaPlayer rejects a started successor. The rate and pitch
                    // are applied again once this player is promoted to current.
                    var level = (float)_volume;
                    next.SetVolume(level, level);

                    var current = _player;
                    if (current is null)
                    {
                        ClearNextPlayer();
                        return;
                    }

                    current.SetNextMediaPlayer(next);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    ClearNextPlayer();
                }
            };

            next.Completion += (_, _) =>
            {
                // Only reached when this player finishes without a successor primed.
                if (ReferenceEquals(_player, next) && _nextPlayer is null)
                {
                    PlaybackEnded?.Invoke(this, EventArgs.Empty);
                }
            };

            next.Error += (_, args) =>
            {
                args.Handled = true;
                System.Diagnostics.Debug.WriteLine(
                    $"Gapless successor failed (what={args.What}, extra={args.Extra}).");

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // A refused file simply keeps the normal end-of-track path.
                    if (ReferenceEquals(_nextPlayer, next))
                    {
                        ClearNextPlayer();
                    }
                });
            };

            next.SetDataSource(filePath);
            _nextPlayer = next;
            _nextPath = filePath;
            next.PrepareAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ClearNextPlayer();
        }
    }

    private void ClearNextPlayer()
    {
        var next = _nextPlayer;
        _nextPlayer = null;
        _nextPath = null;
        _gaplessPromotionPending = false;

        if (next is null)
        {
            return;
        }

        try
        {
            _player?.SetNextMediaPlayer(null);
        }
        catch
        {
            // The current player may already be reset after a transition.
        }

        ReleasePlayerInstance(next);
    }

    /// <summary>
    /// The primed player reports playback only once Android has switched to it, so this is
    /// the signal to adopt it as the current player. Cheap enough to poll from the position
    /// timer that already runs four times a second.
    /// </summary>
    private void CheckGaplessTransition()
    {
        if (_gaplessPromotionPending || _disposed)
        {
            return;
        }

        var next = _nextPlayer;
        if (next is null || _player is null || !IsPlayingSafe(next))
        {
            return;
        }

        _gaplessPromotionPending = true;

        // Transports and the position timer already run on the main thread; adopting the
        // new player right here keeps pause/seek from touching the reset old one.
        if (MainThread.IsMainThread)
        {
            PromoteNextPlayer();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(PromoteNextPlayer);
        }
    }

    private static bool IsPlayingSafe(MediaPlayer player)
    {
        try
        {
            return player.IsPlaying;
        }
        catch
        {
            return false;
        }
    }

    private void PromoteNextPlayer()
    {
        _gaplessPromotionPending = false;

        var next = _nextPlayer;
        var previous = _player;
        var path = _nextPath;

        if (next is null || previous is null || ReferenceEquals(next, previous))
        {
            return;
        }

        _nextPlayer = null;
        _nextPath = null;
        _player = next;

        try
        {
            previous.SetNextMediaPlayer(null);
        }
        catch
        {
            // Android already detached the finished player.
        }

        ReleasePlayerInstance(previous);

        try
        {
            var level = (float)_volume;
            next.SetVolume(level, level);
        }
        catch
        {
            // Volume was applied when the successor was prepared.
        }

        // The equalizer is bound to the old audio session, so it has to follow the new player.
        ReleaseEqualizer();
        EnsureEqualizer(next);
        ApplyPlaybackParams();
        ApplyEqualizerState();

        GaplessTrackStarted?.Invoke(this, new GaplessTransitionEventArgs(path ?? string.Empty));
    }

    private static AudioAttributes CreateAudioAttributes() =>
        new AudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;

    private void RestoreVolume()
    {
        try
        {
            var level = (float)_volume;
            _player?.SetVolume(level, level);
            _nextPlayer?.SetVolume(level, level);
        }
        catch
        {
            // A released player can race with shutdown; volume is not critical.
        }
    }

    private void ApplyPlaybackParams()
    {
        if (_disposed)
        {
            return;
        }

        // Only the current player: PlaybackParams would start a primed successor, which
        // breaks SetNextMediaPlayer. The successor picks the params up when it is promoted.
        if (_player is { } player)
        {
            ApplyPlaybackParamsTo(player);
        }
    }

    private void ApplyPlaybackParamsTo(MediaPlayer player)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        try
        {
            var parameters = new PlaybackParams();
            parameters.SetSpeed((float)_playbackRate);
            parameters.SetPitch((float)_pitch);
            player.PlaybackParams = parameters;
        }
        catch (Exception ex)
        {
            // Some hardware decoders reject speed/pitch; the rest of playback is unaffected.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void EnsureEqualizer(MediaPlayer player)
    {
        if (_equalizer is not null || _disposed)
        {
            return;
        }

        try
        {
            if (player.AudioSessionId == 0)
            {
                return;
            }

            _equalizer = new Equalizer(0, player.AudioSessionId);
            _equalizerPresets.Clear();

            var presetCount = _equalizer.NumberOfPresets;
            for (short i = 0; i < presetCount; i++)
            {
                var name = _equalizer.GetPresetName(i);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _equalizerPresets.Add(name);
                }
            }

            ApplyEqualizerState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ReleaseEqualizer();
        }
    }

    private void ApplyEqualizerState()
    {
        var equalizer = _equalizer;
        if (equalizer is null || _disposed)
        {
            return;
        }

        try
        {
            equalizer.SetEnabled(_equalizerEnabled);

            if (!_equalizerEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_equalizerPreset) && _equalizerPresets.Count > 0)
            {
                _equalizerPreset = _equalizerPresets[0];
            }

            var presetCount = equalizer.NumberOfPresets;
            for (short i = 0; i < presetCount; i++)
            {
                if (string.Equals(equalizer.GetPresetName(i), _equalizerPreset, StringComparison.OrdinalIgnoreCase))
                {
                    equalizer.UsePreset(i);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Effects are optional; never let a vendor EQ crash playback.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void ReleaseEqualizer()
    {
        var equalizer = _equalizer;
        _equalizer = null;
        _equalizerPresets.Clear();

        if (equalizer is null)
        {
            return;
        }

        try
        {
            equalizer.SetEnabled(false);
        }
        catch
        {
            // The audio session may already be gone.
        }

        try
        {
            equalizer.Release();
        }
        catch
        {
            // Vendor implementations can throw while tearing down.
        }

        equalizer.Dispose();
    }

    private void ReleasePlayer()
    {
        ClearNextPlayer();
        ReleaseEqualizer();

        var player = _player;
        _player = null;

        if (player is null)
        {
            return;
        }

        ReleasePlayerInstance(player);
    }

    private static void ReleasePlayerInstance(MediaPlayer player)
    {
        try
        {
            player.Reset();
        }
        catch
        {
            // Reset is illegal in some states; Release is still safe afterwards.
        }

        try
        {
            player.Release();
        }
        catch
        {
            // Nothing useful can be done if the native player is already gone.
        }

        player.Dispose();
    }

    // ------------------------------------------------- audio focus

    /// <summary>
    /// Asks Android for music focus before every start. When another app currently owns
    /// audio (call, assistant, another player) the request is refused and playback does not
    /// begin, instead of two apps talking over each other.
    /// </summary>
    private bool TryRequestAudioFocus()
    {
        var manager = _audioManager;
        if (manager is null)
        {
            return true;
        }

        if (_hasAudioFocus)
        {
            return true;
        }

        _focusListener ??= new FocusChangeListener(this);

        try
        {
#pragma warning disable CA1422 // The legacy overload still works and is honoured on all supported releases.
            var result = manager.RequestAudioFocus(_focusListener, Android.Media.Stream.Music, AudioFocus.Gain);
#pragma warning restore CA1422
            _hasAudioFocus = result == AudioFocusRequest.Granted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _hasAudioFocus = false;
        }

        return _hasAudioFocus;
    }

    private void AbandonAudioFocus()
    {
        _pausedForFocus = false;
        _duckedForFocus = false;

        var manager = _audioManager;
        if (manager is null || !_hasAudioFocus)
        {
            return;
        }

        _hasAudioFocus = false;

        try
        {
#pragma warning disable CA1422
            manager.AbandonAudioFocus(_focusListener);
#pragma warning restore CA1422
        }
        catch
        {
            // Focus is already gone with the audio session.
        }
    }

    /// <summary>Runs on the main thread from <see cref="FocusChangeListener"/>.</summary>
    private void OnAudioFocusChanged(AudioFocus change)
    {
        if (_disposed)
        {
            return;
        }

        switch (change)
        {
            case AudioFocus.Loss:
                // A call or another player took audio for good; do not resume by itself.
                _hasAudioFocus = false;
                _pausedForFocus = false;
                _duckedForFocus = false;
                PauseForFocus();
                break;

            case AudioFocus.LossTransient:
                // Navigation prompt, Reels, a short clip elsewhere: pause, then continue the
                // same song from the same spot when focus returns.
                _hasAudioFocus = false;
                _pausedForFocus = true;
                _duckedForFocus = false;
                PauseForFocus();
                break;

            case AudioFocus.LossTransientCanDuck:
                _hasAudioFocus = false;
                _duckedForFocus = true;
                try
                {
                    _player?.SetVolume(0.2f, 0.2f);
                }
                catch
                {
                    // Some decoders reject volume changes mid-stream.
                }

                break;

            case AudioFocus.Gain:
                _hasAudioFocus = true;

                if (_duckedForFocus)
                {
                    _duckedForFocus = false;
                    RestoreVolume();
                }

                if (!_pausedForFocus)
                {
                    break;
                }

                _pausedForFocus = false;

                try
                {
                    _player?.Start();
                    ApplyPlaybackParams();
                    AudioFocusGained?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }

                break;
        }
    }

    private void PauseForFocus()
    {
        try
        {
            _player?.Pause();
        }
        catch
        {
            // The media may still be preparing.
        }

        AudioFocusLost?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------- headset / band disconnect

    /// <summary>
    /// Listens for the two signals Android gives when audio output changes unexpectedly:
    /// the ACTION_AUDIO_BECOMING_NOISY broadcast (unplugged wired headset) and output
    /// device removal (Bluetooth band, USB DAC, hearing aid). Either one must pause playback
    /// before the song continues through the phone speaker.
    /// </summary>
    private void StartDisconnectMonitoring()
    {
        if (_disconnectMonitoring)
        {
            return;
        }

        try
        {
            var context = Android.App.Application.Context;
            if (context is null)
            {
                return;
            }

            _audioManager = context.GetSystemService(Context.AudioService) as AudioManager;
            _noisyReceiver = new BecomingNoisyReceiver(this);

            var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
            AndroidX.Core.Content.ContextCompat.RegisterReceiver(
                context,
                _noisyReceiver,
                filter,
                AndroidX.Core.Content.ContextCompat.ReceiverNotExported);

            // Device callbacks exist from API 23; the noisy broadcast still covers older
            // releases for wired headsets.
            if (_audioManager is not null && OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                _deviceCallback = new OutputDeviceCallback(this);
                _audioManager.RegisterAudioDeviceCallback(_deviceCallback, null);
            }

            _disconnectMonitoring = true;
        }
        catch (Exception ex)
        {
            // A device that rejects the receiver still plays music; never crash over this.
            System.Diagnostics.Debug.WriteLine(ex);
            StopDisconnectMonitoring();
        }
    }

    private void StopDisconnectMonitoring()
    {
        _disconnectMonitoring = false;

        var receiver = _noisyReceiver;
        _noisyReceiver = null;
        if (receiver is not null)
        {
            try
            {
                Android.App.Application.Context.UnregisterReceiver(receiver);
            }
            catch
            {
                // The process may already be shutting down.
            }

            receiver.Dispose();
        }

        var callback = _deviceCallback;
        _deviceCallback = null;
        if (callback is not null)
        {
            try
            {
                // Registering happened behind the same API 23 guard (StartDisconnectMonitoring).
                if (OperatingSystem.IsAndroidVersionAtLeast(23))
                {
                    _audioManager?.UnregisterAudioDeviceCallback(callback);
                }
            }
            catch
            {
                // Audio manager can already be torn down.
            }

            callback.Dispose();
        }

        _audioManager = null;
    }

    /// <summary>Called from the platform receiver/callback on a binder thread.</summary>
    private void NotifyAudioDeviceDisconnected(bool waitForRouteChange)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (waitForRouteChange)
            {
                // Audio routes settle a moment after the noisy broadcast; checking right away
                // would still see the old device and pause during a normal output switch.
                await Task.Delay(350);
            }

            if (_disposed || HasPeripheralOutput())
            {
                return;
            }

            AudioDeviceDisconnected?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// True while any wired headset, Bluetooth speaker/headset or USB output is still
    /// connected, so switching between two accessories never interrupts the song.
    /// </summary>
    private bool HasPeripheralOutput()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return false;
        }

        try
        {
            var devices = _audioManager?.GetDevices(GetDevicesTargets.Outputs);
            if (devices is null)
            {
                return false;
            }

            foreach (var device in devices)
            {
                if (IsPeripheralOutput(device.Type))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Audio service may be restarting; assume nothing is left.
        }

        return false;
    }

    private static bool IsPeripheralOutput(AudioDeviceType type)
    {
        // The device-callback path only exists from API 23; the enum members below are
        // annotated per API level, so each group is stated behind the matching guard.
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return false;
        }

        if (type is AudioDeviceType.WiredHeadset or
            AudioDeviceType.WiredHeadphones or
            AudioDeviceType.BluetoothA2dp or
            AudioDeviceType.BluetoothSco or
            AudioDeviceType.UsbDevice or
            AudioDeviceType.UsbAccessory or
            AudioDeviceType.LineAnalog or
            AudioDeviceType.LineDigital)
        {
            return true;
        }

        // Hearing aids and LE audio devices connect/disconnect on their own schedule and
        // must never stop the song; they are deliberately not treated as removable output.
        return OperatingSystem.IsAndroidVersionAtLeast(26) &&
               type is AudioDeviceType.UsbHeadset;
    }

    /// <summary>Receives Android's focus decisions and forwards them to the player thread.</summary>
    private sealed class FocusChangeListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly AndroidAudioPlayer _owner;

        public FocusChangeListener(AndroidAudioPlayer owner) => _owner = owner;

        public void OnAudioFocusChange(AudioFocus focusChange) =>
            MainThread.BeginInvokeOnMainThread(() => _owner.OnAudioFocusChanged(focusChange));
    }

    private sealed class BecomingNoisyReceiver : BroadcastReceiver
    {
        private readonly AndroidAudioPlayer _owner;

        public BecomingNoisyReceiver(AndroidAudioPlayer owner) => _owner = owner;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (string.Equals(
                    intent?.Action,
                    AudioManager.ActionAudioBecomingNoisy,
                    StringComparison.Ordinal))
            {
                _owner.NotifyAudioDeviceDisconnected(waitForRouteChange: true);
            }
        }
    }

    /// <summary>Catches Bluetooth/LE band and USB output removal that does not broadcast noise.</summary>
    private sealed class OutputDeviceCallback : AudioDeviceCallback
    {
        private readonly AndroidAudioPlayer _owner;

        public OutputDeviceCallback(AndroidAudioPlayer owner) => _owner = owner;

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            if (removedDevices is null || !OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                return;
            }

            foreach (var device in removedDevices)
            {
                if (IsPeripheralOutput(device.Type))
                {
                    _owner.NotifyAudioDeviceDisconnected(waitForRouteChange: false);
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AbandonAudioFocus();
        StopDisconnectMonitoring();
        ReleasePlayer();
    }
}
#endif
