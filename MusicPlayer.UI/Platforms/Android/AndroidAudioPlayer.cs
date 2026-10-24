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
    private Equalizer? _equalizer;
    private readonly List<string> _equalizerPresets = [];
    private AudioManager? _audioManager;
    private BecomingNoisyReceiver? _noisyReceiver;
    private OutputDeviceCallback? _deviceCallback;
    private bool _disconnectMonitoring;
    private bool _disposed;
    private bool _equalizerEnabled;
    private string _equalizerPreset = string.Empty;
    private double _playbackRate = 1;
    private double _pitch = 1;

    public event EventHandler? AudioDeviceDisconnected;

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
                var attributes = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!;

                player.SetAudioAttributes(attributes);

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
                            player.Start();
                            ApplyPlaybackParams();
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
                    if (ReferenceEquals(_player, player))
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
            var player = _player ?? throw new InvalidOperationException("No audio is loaded.");
            player.Start();
        });

    public Task PauseAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_disposed)
            {
                _player?.Pause();
            }
        });

    public Task StopAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_disposed)
            {
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

        var level = (float)Math.Clamp(volume, 0, 1);
        try
        {
            _player?.SetVolume(level, level);
        }
        catch
        {
            // A released player can race with shutdown; volume is not critical.
        }
    }

    private void ApplyPlaybackParams()
    {
        if (_disposed || _player is null || !OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        try
        {
            var parameters = new PlaybackParams();
            parameters.SetSpeed((float)_playbackRate);
            parameters.SetPitch((float)_pitch);
            _player.PlaybackParams = parameters;
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
        ReleaseEqualizer();

        var player = _player;
        _player = null;

        if (player is null)
        {
            return;
        }

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
    private void NotifyAudioDeviceDisconnected()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_disposed)
            {
                AudioDeviceDisconnected?.Invoke(this, EventArgs.Empty);
            }
        });
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

        if (OperatingSystem.IsAndroidVersionAtLeast(26) &&
            type is AudioDeviceType.UsbHeadset)
        {
            return true;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(28) &&
            type is AudioDeviceType.HearingAid)
        {
            return true;
        }

        return OperatingSystem.IsAndroidVersionAtLeast(31) &&
               type is AudioDeviceType.BleHeadset or AudioDeviceType.BleSpeaker;
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
                _owner.NotifyAudioDeviceDisconnected();
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
                    _owner.NotifyAudioDeviceDisconnected();
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
        StopDisconnectMonitoring();
        ReleasePlayer();
    }
}
#endif
