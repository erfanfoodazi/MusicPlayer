#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Domain.Models;
using Microsoft.Maui.ApplicationModel;
using MusicPlayer.UI.Services.Playback;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;
using AndroidPlaybackStateCode = Android.Media.Session.PlaybackStateCode;
using AndroidColor = Android.Graphics.Color;
using AndroidMediaMetadata = Android.Media.MediaMetadata;
using AndroidPaint = Android.Graphics.Paint;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Foreground media service. It keeps the process alive while music plays and exposes a
/// MediaSession so Android's lock screen, notification shade, Bluetooth controls and the
/// Samsung Now Bar all control the same PlayerService singleton.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class MediaPlaybackService : Service
{
    public const string ChannelId = "auralis_playback";
    public const string ActionPrevious = "com.musicplayer.app.PREVIOUS";
    public const string ActionPlayPause = "com.musicplayer.app.PLAY_PAUSE";
    public const string ActionNext = "com.musicplayer.app.NEXT";

    private const int NotificationId = 2201;
    private const int ContentIntentRequestCode = 100;
    private const int LockScreenIntentRequestCode = 101;
    private const int ArtworkSize = 512;
    private const int ArtworkDecodeCeiling = 1024;
    private const string KeyTitle = "android.media.metadata.TITLE";
    private const string KeyArtist = "android.media.metadata.ARTIST";
    private const string KeyAlbum = "android.media.metadata.ALBUM";
    private const string KeyDuration = "android.media.metadata.DURATION";
    private const string KeyArtwork = "android.media.metadata.ART";

    private MediaSession? _session;
    private PlayerService? _player;
    private PendingIntent? _contentIntent;
    private Bitmap? _artwork;
    private Bitmap? _placeholderArtwork;
    private string? _artworkSourcePath;
    private Music? _metadataTrack;
    private double _metadataDuration;
    private DateTime _lastProgressStateUpdate = DateTime.MinValue;
    private bool _subscribed;
    private ScreenStateReceiver? _screenReceiver;

    internal static MediaPlaybackService? Instance { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();

        Instance = this;
        CreateNotificationChannel();
        RegisterScreenReceiver();

        _player = PlayerService.Instance;
        if (!_subscribed)
        {
            _player.PropertyChanged += OnPlayerPropertyChanged;
            _subscribed = true;
        }

        _session = new MediaSession(this, "Auralis");
        _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _session.SetCallback(new SessionCallback());
        _session.Active = true;

        _contentIntent = CreateContentIntent();
        if (_contentIntent is not null)
        {
            _session.SetSessionActivity(_contentIntent);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        HandleAction(intent?.Action);
        Refresh(startForeground: true);
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        UnregisterScreenReceiver();

        if (_subscribed && _player is not null)
        {
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _subscribed = false;
        }

        var session = _session;
        if (session is not null)
        {
            try
            {
                session.Active = false;
                session.SetCallback(null);
                session.Release();
            }
            catch
            {
                // The platform may already have torn the session down.
            }

            session.Dispose();
        }

        _session = null;

        ReleaseArtwork();
        Instance = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Listens for the display coming up so the player can be the surface the user lands on.
    /// The broadcast arrives after the screen is already on, and nothing below touches the
    /// power state, so the app can never wake a sleeping phone by itself.
    /// </summary>
    private void RegisterScreenReceiver()
    {
        try
        {
            _screenReceiver = new ScreenStateReceiver(OnScreenTurnedOn);
            var filter = new IntentFilter(Intent.ActionScreenOn);

            AndroidX.Core.Content.ContextCompat.RegisterReceiver(
                this,
                _screenReceiver,
                filter,
                AndroidX.Core.Content.ContextCompat.ReceiverNotExported);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _screenReceiver = null;
        }
    }

    private void UnregisterScreenReceiver()
    {
        var receiver = _screenReceiver;
        _screenReceiver = null;

        if (receiver is null)
        {
            return;
        }

        try
        {
            UnregisterReceiver(receiver);
        }
        catch
        {
            // The process may already have torn the receiver down.
        }
    }

    private void OnScreenTurnedOn()
    {
        if (!HasPlayableTrack())
        {
            return;
        }

        // The keyguard is reported a beat after the display comes up. Asking too early would
        // open the player over a phone that is still unlocking itself.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(400);

            if (!IsLockedAndAwake() || !HasPlayableTrack())
            {
                return;
            }

            OpenLockPage();
        });
    }

    private static bool HasPlayableTrack()
    {
        var player = PlayerService.Instance;
        return player.CurrentTrack is not null
               && player.State is PlaybackState.Playing or PlaybackState.Paused;
    }

    private bool IsLockedAndAwake()
    {
        var keyguard = (KeyguardManager?)GetSystemService(KeyguardService);
        if (keyguard?.IsKeyguardLocked != true)
        {
            return false;
        }

        var power = (PowerManager?)GetSystemService(PowerService);
        return power?.IsInteractive == true;
    }

    /// <summary>
    /// The screen is already on, so the activity only has to draw over the keyguard that is
    /// showing. No notification, wake lock or turn-screen-on flag is involved.
    /// </summary>
    private void OpenLockPage()
    {
        try
        {
            var intent = new Intent(this, typeof(MusicPlayer.UI.MainActivity))
                .SetAction(MusicPlayer.UI.MainActivity.ActionOpenLockScreen)
                .PutExtra(MusicPlayer.UI.MainActivity.ExtraOpenLockScreen, true)
                .SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

            StartActivity(intent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>Re-reads track, state and artwork and refreshes session/notification.</summary>
    public void Refresh(bool startForeground = false)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => Refresh(startForeground));
            return;
        }

        var player = PlayerService.Instance;
        var track = player.CurrentTrack;

        if (track is null || _session is null)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                {
                    StopForeground(StopForegroundFlags.Remove);
                }
                else
                {
#pragma warning disable CA1422 // Required on API 21-23.
                    StopForeground(true);
#pragma warning restore CA1422
                }
            }
            catch
            {
                // No foreground notification is attached yet.
            }

            StopSelf();
            return;
        }

        UpdateMetadata(player, track);
        UpdatePlaybackState(player, force: true);

        var notification = BuildNotification(player, track);
        if (notification is null)
        {
            return;
        }

        if (startForeground)
        {
            StartForeground(NotificationId, notification);
        }
        else
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(NotificationId, notification);
        }
    }

    /// <summary>Called from the player's 400 ms clock; throttled so the binder is not hammered.</summary>
    public void UpdateProgress()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(UpdateProgress);
            return;
        }

        if ((DateTime.UtcNow - _lastProgressStateUpdate).TotalMilliseconds < 1500)
        {
            return;
        }

        UpdatePlaybackState(PlayerService.Instance, force: true);
    }

    private void HandleAction(string? action)
    {
        var player = PlayerService.Instance;

        switch (action)
        {
            case ActionPrevious:
                player.PlayPrevious();
                break;
            case ActionPlayPause:
                player.TogglePlayPause();
                break;
            case ActionNext:
                player.PlayNext();
                break;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerService.CurrentTrack)
            or nameof(PlayerService.State)
            or nameof(PlayerService.Duration))
        {
            Refresh();
        }
    }

    private void UpdateMetadata(PlayerService player, Music track)
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            return;
        }

        var duration = player.Duration;
        var artworkPath = ResolveArtworkPath(track);

        if (ReferenceEquals(track, _metadataTrack) &&
            Math.Abs(duration - _metadataDuration) < 0.5 &&
            string.Equals(artworkPath, _artworkSourcePath, StringComparison.Ordinal))
        {
            return;
        }

        var builder = new AndroidMediaMetadata.Builder();
        builder.PutString(KeyTitle, track.Name);
        builder.PutString(KeyArtist, track.ArtistName);
        builder.PutString(KeyAlbum, track.Album?.Title ?? string.Empty);
        builder.PutLong(KeyDuration, (long)Math.Max(0, duration * 1000));

        // Always publish artwork for the current track. If this song has no cover, use a
        // generated Auralis placeholder instead of leaving the previous bitmap in the
        // session - Samsung's Now Bar, the lock screen and Bluetooth clients otherwise keep
        // showing the last song's cover.
        _artwork = LoadArtwork(artworkPath) ?? GetPlaceholderArtwork();
        if (_artwork is not null)
        {
            builder.PutBitmap(KeyArtwork, _artwork);
        }

        session.SetMetadata(builder.Build());
        _metadataTrack = track;
        _metadataDuration = duration;
        _artworkSourcePath = artworkPath;
    }

    /// <summary>
    /// Uses the full extracted image for system UI, then falls back to the cached 512 px
    /// thumbnail. This avoids publishing the low-resolution list thumbnail to the lock
    /// screen while still keeping the bitmap parcel small.
    /// </summary>
    private static string? ResolveArtworkPath(Music track)
    {
        string?[] candidates =
        [
            track.FullArtworkPath,
            track.ArtworkPath,
            track.Album?.ArtworkPath,
        ];

        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Bitmap? LoadArtwork(string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath))
        {
            return null;
        }

        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(artworkPath, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            {
                return null;
            }

            // Decode to at most 1024 px, then apply one smooth scale to 512 px. Sampling
            // alone would make odd-sized covers soft.
            var longest = Math.Max(bounds.OutWidth, bounds.OutHeight);
            var sampleSize = 1;
            while (longest / sampleSize > ArtworkDecodeCeiling)
            {
                sampleSize *= 2;
            }

            using var decoded = BitmapFactory.DecodeFile(
                artworkPath,
                new BitmapFactory.Options { InSampleSize = sampleSize });
            if (decoded is null)
            {
                return null;
            }

            var decodedLongest = Math.Max(decoded.Width, decoded.Height);
            if (decodedLongest <= ArtworkSize)
            {
                return decoded.Copy(decoded.GetConfig() ?? Bitmap.Config.Argb8888!, false);
            }

            var scale = ArtworkSize / (double)decodedLongest;
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            return Bitmap.CreateScaledBitmap(decoded, width, height, true);
        }
        catch
        {
            // A broken cover must not break metadata updates; the placeholder takes over.
            return null;
        }
    }

    private Bitmap? GetPlaceholderArtwork()
    {
        if (_placeholderArtwork is { IsRecycled: false } existing)
        {
            return existing;
        }

        try
        {
            var bitmap = Bitmap.CreateBitmap(ArtworkSize, ArtworkSize, Bitmap.Config.Argb8888!);
            if (bitmap is null)
            {
                return null;
            }

            using var canvas = new Canvas(bitmap);
            canvas.DrawColor(AndroidColor.ParseColor("#141020"));

            using var disc = new AndroidPaint(PaintFlags.AntiAlias)
            {
                Color = AndroidColor.ParseColor("#241C3F"),
            };
            canvas.DrawCircle(ArtworkSize / 2f, 238f, 154f, disc);

            using var note = new AndroidPaint(PaintFlags.AntiAlias)
            {
                Color = AndroidColor.ParseColor("#A78BFF"),
                TextSize = 196f,
                TextAlign = AndroidPaint.Align.Center,
            };
            note.SetTypeface(Typeface.Default);
            canvas.DrawText("\u266B", ArtworkSize / 2f, 306f, note);

            using var brand = new AndroidPaint(PaintFlags.AntiAlias)
            {
                Color = AndroidColor.ParseColor("#8F8FA4"),
                TextSize = 25f,
                TextAlign = AndroidPaint.Align.Center,
                LetterSpacing = 0.16f,
            };
            brand.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
            canvas.DrawText("AURALIS", ArtworkSize / 2f, 454f, brand);

            _placeholderArtwork = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePlaybackState(PlayerService player, bool force = false)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        if (!force && (DateTime.UtcNow - _lastProgressStateUpdate).TotalMilliseconds < 1500)
        {
            return;
        }

        _lastProgressStateUpdate = DateTime.UtcNow;

        var code = player.State switch
        {
            PlaybackState.Playing => AndroidPlaybackStateCode.Playing,
            PlaybackState.Paused => AndroidPlaybackStateCode.Paused,
            PlaybackState.Buffering => AndroidPlaybackStateCode.Buffering,
            _ => AndroidPlaybackStateCode.Stopped
        };

        var actions = AndroidPlaybackState.ActionPlay
                      | AndroidPlaybackState.ActionPause
                      | AndroidPlaybackState.ActionPlayPause
                      | AndroidPlaybackState.ActionSkipToNext
                      | AndroidPlaybackState.ActionSkipToPrevious
                      | AndroidPlaybackState.ActionSeekTo
                      | AndroidPlaybackState.ActionStop;

        var builder = new AndroidPlaybackState.Builder();
        builder.SetActions(actions);
        builder.SetState(
            code,
            (long)Math.Max(0, player.Position * 1000),
            code == AndroidPlaybackStateCode.Playing ? 1f : 0f);

        session.SetPlaybackState(builder.Build());
    }

    private Notification? BuildNotification(PlayerService player, Music track)
    {
        var session = _session;
        if (session is null)
        {
            return null;
        }

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder
            .SetSmallIcon(Resource.Drawable.ic_stat_music)
            .SetContentTitle(track.Name)
            .SetContentText(track.ArtistName)
            .SetSubText(track.Album?.Title)
            .SetContentIntent(_contentIntent)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryTransport)
            .SetVisibility(NotificationVisibility.Public);

        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
#pragma warning disable CA1422 // Priority is the pre-channel equivalent of Low importance.
            builder.SetPriority((int)NotificationPriority.Low);
#pragma warning restore CA1422
        }

        if (_artwork is not null)
        {
            builder.SetLargeIcon(_artwork);
        }

        builder.AddAction(CreateAction(
            Android.Resource.Drawable.IcMediaPrevious,
            "Previous",
            ActionPrevious,
            1));

        builder.AddAction(CreateAction(
            player.State == PlaybackState.Playing
                ? Android.Resource.Drawable.IcMediaPause
                : Android.Resource.Drawable.IcMediaPlay,
            player.State == PlaybackState.Playing ? "Pause" : "Play",
            ActionPlayPause,
            2));

        builder.AddAction(CreateAction(
            Android.Resource.Drawable.IcMediaNext,
            "Next",
            ActionNext,
            3));

        if (CreateLockScreenIntent() is { } lockScreenIntent)
        {
            builder.AddAction(CreateAction(
                Resource.Drawable.ic_lyrics,
                "Lyrics",
                lockScreenIntent));
        }

        var style = new Notification.MediaStyle();
        style.SetMediaSession(session.SessionToken);
        style.SetShowActionsInCompactView(0, 1, 2);
        builder.SetStyle(style);

        return builder.Build();
    }

    private Notification.Action CreateAction(int iconResource, string title, string action, int requestCode)
    {
        var intent = new Intent(this, typeof(MediaPlaybackService)).SetAction(action);
        var pendingIntent = PendingIntent.GetService(this, requestCode, intent, ImmutablePendingIntentFlags);
        return CreateAction(iconResource, title, pendingIntent!);
    }

    private Notification.Action CreateAction(int iconResource, string title, PendingIntent pendingIntent)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return new Notification.Action.Builder(
                Android.Graphics.Drawables.Icon.CreateWithResource(this, iconResource),
                Java.Lang.String.ValueOf(title),
                pendingIntent).Build();
        }

#pragma warning disable CA1422 // Required on API 21-22.
        return new Notification.Action.Builder(iconResource, Java.Lang.String.ValueOf(title), pendingIntent).Build();
#pragma warning restore CA1422
    }

    private PendingIntent? CreateContentIntent()
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        if (launchIntent is null)
        {
            return null;
        }

        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, ContentIntentRequestCode, launchIntent, ImmutablePendingIntentFlags);
    }

    private PendingIntent? CreateLockScreenIntent()
    {
        var intent = new Intent(this, typeof(MusicPlayer.UI.MainActivity))
            .SetAction(MusicPlayer.UI.MainActivity.ActionOpenLockScreen);

        intent.PutExtra(MusicPlayer.UI.MainActivity.ExtraOpenLockScreen, true);
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        return PendingIntent.GetActivity(this, LockScreenIntentRequestCode, intent, ImmutablePendingIntentFlags);
    }

    private static PendingIntentFlags ImmutablePendingIntentFlags =>
        PendingIntentFlags.UpdateCurrent |
        (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0);

    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null)
        {
            return;
        }

        var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
        {
            Description = "Auralis playback controls",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private void ReleaseArtwork()
    {
        var artwork = _artwork;
        var placeholder = _placeholderArtwork;
        _artwork = null;
        _placeholderArtwork = null;

        ReleaseBitmap(artwork);

        if (!ReferenceEquals(placeholder, artwork))
        {
            ReleaseBitmap(placeholder);
        }
    }

    private static void ReleaseBitmap(Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        try
        {
            bitmap.Recycle();
        }
        catch
        {
            // A bitmap already handed to the system can reject recycle().
        }

        bitmap.Dispose();
    }

    private sealed class SessionCallback : MediaSession.Callback
    {
        public override void OnPlay() => MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.Resume());

        public override void OnPause() => MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.Pause());

        public override void OnSkipToNext() => MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.PlayNext());

        public override void OnSkipToPrevious() => MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.PlayPrevious());

        public override void OnSeekTo(long pos) =>
            MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.Seek(pos / 1000.0));

        public override void OnStop() => MainThread.BeginInvokeOnMainThread(() => PlayerService.Instance.Stop());
    }
}

/// <summary>
/// Small static entry point for the shared PlayerService. Everything runs on the UI thread
/// because Android's MediaSession and Notification builders are main-thread objects.
/// </summary>
public static class MediaPlaybackBridge
{
    public static void Refresh()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(Refresh);
            return;
        }

        if (PlayerService.Instance.CurrentTrack is null)
        {
            MediaPlaybackService.Instance?.Refresh();
            return;
        }

        if (MediaPlaybackService.Instance is null)
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(MediaPlaybackService));

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            return;
        }

        MediaPlaybackService.Instance.Refresh();
    }

    public static void UpdateProgress()
    {
        MediaPlaybackService.Instance?.UpdateProgress();
    }
}
#endif
