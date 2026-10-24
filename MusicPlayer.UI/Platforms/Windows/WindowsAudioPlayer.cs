#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;

namespace MusicPlayer.UI.Services.Playback;

/// <summary>
/// Windows implementation backed by the WinRT <see cref="MediaPlayer"/>. It supports the
/// formats the installed Windows media stack can decode (MP3, WAV, M4A/AAC, FLAC, WMA, etc.).
/// </summary>
public sealed class WindowsAudioPlayer : IAudioPlayer
{
    private readonly MediaPlayer _player = new();
    private bool _disposed;
    private double _playbackRate = 1;
    private DeviceWatcher? _endpointWatcher;
    private string? _defaultRenderId;

    public bool SupportsPlaybackRate => true;

    public void SetPlaybackRate(double rate)
    {
        if (_disposed)
        {
            return;
        }

        _playbackRate = Math.Clamp(rate, 0.5, 2);
        try
        {
            var session = _player.PlaybackSession;
            if (session is not null)
            {
                session.PlaybackRate = _playbackRate;
            }
        }
        catch
        {
            // Some codecs reject rate changes while opening; MediaOpened reapplies it.
        }
    }

    public WindowsAudioPlayer()
    {
        _player.AutoPlay = false;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        StartEndpointWatch();
    }

    public double PositionSeconds
    {
        get
        {
            try
            {
                return _player.PlaybackSession?.Position.TotalSeconds ?? 0;
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
                var duration = _player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
                return Math.Max(0, duration.TotalSeconds);
            }
            catch
            {
                return 0;
            }
        }
    }

    public event EventHandler? PlaybackEnded;
    public event EventHandler<AudioPlayerFailureEventArgs>? Failed;

    /// <summary>
    /// Windows has no "becoming noisy" broadcast, so the app watches the audio-endpoint
    /// device list instead: when the endpoint that is currently the default output (a USB
    /// or Bluetooth headset, a band, a DAC) disappears, the song must not continue through
    /// the speakers. <see cref="PlayerService"/> pauses on this event.
    /// </summary>
    public event EventHandler? AudioDeviceDisconnected;

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

        void MediaOpened(MediaPlayer sender, object args)
        {
            try
            {
                var session = sender.PlaybackSession;
                if (session is not null)
                {
                    session.PlaybackRate = _playbackRate;
                }
            }
            catch
            {
                // Rate is an enhancement; normal playback must still continue.
            }

            var duration = sender.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
            completion.TrySetResult(duration);
        }

        void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? "Windows Media Player could not open this file."
                : args.ErrorMessage;

            completion.TrySetException(
                new InvalidOperationException(message, args.ExtendedErrorCode));
        }

        _player.MediaOpened += MediaOpened;
        _player.MediaFailed += MediaFailed;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // The default output can change between tracks; re-cache it so a removed
                // device is compared against what is actually in use right now.
                RefreshDefaultRenderId();

                _player.Source = MediaSource.CreateFromUri(
                    new Uri(Path.GetFullPath(filePath)));

                if (autoPlay)
                {
                    _player.Play();
                }
            });

            using var registration = cancellationToken.Register(() =>
            {
                // Do not touch _player here: the caller may already be loading the next
                // track into the same MediaPlayer. StopAsync handles an explicit stop.
                completion.TrySetCanceled(cancellationToken);
            });

            // Local files normally open immediately; this guards against a platform
            // decoder that never raises MediaOpened or MediaFailed.
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _player.MediaOpened -= MediaOpened;
            _player.MediaFailed -= MediaFailed;
        }
    }

    public Task PlayAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _player.Play();
        });

    public Task PauseAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!_disposed)
            {
                _player.Pause();
            }
        });

    public Task StopAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            _player.Pause();
            _player.Source = null;
        });

    public Task SeekAsync(double seconds) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            var session = _player.PlaybackSession;
            if (session is not null)
            {
                session.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        });

    public void SetVolume(double volume)
    {
        if (_disposed)
        {
            return;
        }

        _player.Volume = Math.Clamp(volume, 0, 1);
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? "Windows Media Player reported a playback error."
            : args.ErrorMessage;

        Failed?.Invoke(
            this,
            new AudioPlayerFailureEventArgs(
                new InvalidOperationException(message, args.ExtendedErrorCode)));
    }

    // ------------------------------------------------- headset / band disconnect

    /// <summary>Starts watching audio endpoints; failures only cost the disconnect feature.</summary>
    private void StartEndpointWatch()
    {
        try
        {
            RefreshDefaultRenderId();

            // DeviceInformation holds the factory; AudioRender class = output endpoints
            // (speakers, USB/BT headsets, bands, DACs).
            _endpointWatcher = DeviceInformation.CreateWatcher(DeviceClass.AudioRender);
            _endpointWatcher.Added += OnEndpointAdded;
            _endpointWatcher.Updated += OnEndpointUpdated;
            _endpointWatcher.Removed += OnEndpointRemoved;
            _endpointWatcher.Start();
        }
        catch (Exception ex)
        {
            // A device service that refuses the watcher still plays music.
            System.Diagnostics.Debug.WriteLine(ex);
            _endpointWatcher = null;
        }
    }

    private void RefreshDefaultRenderId()
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (!string.IsNullOrWhiteSpace(id))
            {
                _defaultRenderId = id;
            }
        }
        catch
        {
            // No audio device at all, or the service is shutting down.
        }
    }

    private void OnEndpointAdded(DeviceWatcher sender, DeviceInformation args)
        => RefreshDefaultRenderId();

    private void OnEndpointUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        => RefreshDefaultRenderId();

    private void OnEndpointRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        // Compare against the endpoint cached as "current" before refreshing: once the
        // system has switched the default away, the removed id would no longer match.
        var removedCurrentOutput = !string.IsNullOrEmpty(_defaultRenderId) &&
                                   string.Equals(
                                       args.Id,
                                       _defaultRenderId,
                                       StringComparison.OrdinalIgnoreCase);

        RefreshDefaultRenderId();

        if (removedCurrentOutput && !_disposed)
        {
            AudioDeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopEndpointWatch()
    {
        var watcher = _endpointWatcher;
        _endpointWatcher = null;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Added -= OnEndpointAdded;
            watcher.Updated -= OnEndpointUpdated;
            watcher.Removed -= OnEndpointRemoved;
            watcher.Stop();
        }
        catch
        {
            // The watcher can already be stopped by the system.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopEndpointWatch();
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.Dispose();
    }
}
#endif
