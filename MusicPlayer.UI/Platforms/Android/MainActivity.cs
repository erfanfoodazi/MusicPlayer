using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.Views;

namespace MusicPlayer.UI;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public const string ActionOpenLockScreen = "com.musicplayer.app.action.OPEN_LOCK_SCREEN";
    public const string ExtraOpenLockScreen = "com.musicplayer.app.extra.OPEN_LOCK_SCREEN";

    private const int PermissionRequest = 4101;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // setShowWhenLocked() has to reach the window before the activity content is created,
        // otherwise Android can draw one frame behind the keyguard during a cold launch.
        if (Intent?.GetBooleanExtra(ExtraOpenLockScreen, false) == true)
        {
            PrepareLockScreenWindow();
        }

        base.OnCreate(savedInstanceState);

        SystemChrome.ApplyAppTheme();
        RequestStartupPermissions();

        if (Microsoft.Maui.Controls.Application.Current is { } app)
        {
            app.RequestedThemeChanged += (_, _) =>
                app.Dispatcher.Dispatch(SystemChrome.ApplyAppTheme);
        }

        HandleLockScreenIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        if (intent is not null)
        {
            HandleLockScreenIntent(intent);
        }
    }

    /// <summary>
    /// The notification's Lyrics action opens this activity over the keyguard. Keeping the
    /// flag scoped to that launch means a normal notification tap still behaves like the app.
    /// </summary>
    private void HandleLockScreenIntent(Intent? intent)
    {
        if (intent?.GetBooleanExtra(ExtraOpenLockScreen, false) != true)
        {
            return;
        }

        intent.RemoveExtra(ExtraOpenLockScreen);

        // The window flags have to be in place before the page is shown: the app can be
        // launched straight from a locked, screen-off phone. The wake lock also covers the
        // case where the activity already exists: changing window flags alone does not always
        // wake a display that the power button has just turned off.
        WakeDisplay();
        PrepareLockScreenWindow();
        MediaPlaybackService.Instance?.DismissLockPageNotification();

        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(
            async () =>
            {
                // A cold start builds the Shell a few hundred milliseconds later; retrying a
                // couple of times is what makes the automatic lock-screen launch reliable.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 420 : 360);

                    var shell = Microsoft.Maui.Controls.Shell.Current;
                    if (shell is null)
                    {
                        continue;
                    }

                    if (shell.CurrentPage is LockScreenPage)
                    {
                        return;
                    }

                    try
                    {
                        await shell.GoToAsync(Routes.LockScreen);
                        return;
                    }
                    catch
                    {
                        // The Shell may still be settling; try again.
                    }
                }
            });
    }

    /// <summary>
    /// Lets the activity sit above the keyguard and wakes the display. Applied as soon as the
    /// lock-screen intent arrives so the page never flashes behind the system lock screen.
    /// </summary>
    private void WakeDisplay()
    {
        try
        {
            var power = (PowerManager?)GetSystemService(PowerService);
            if (power is null || power.IsInteractive)
            {
                return;
            }

#pragma warning disable CA1422 // The screen wake lock is the only API that works on all supported releases.
            var wakeLock = power.NewWakeLock(
                WakeLockFlags.ScreenDim | WakeLockFlags.AcquireCausesWakeup,
                "Auralis:LockScreen");
#pragma warning restore CA1422

            if (wakeLock is null)
            {
                return;
            }

            wakeLock.Acquire(3000);
            _ = ReleaseWakeLockAsync(wakeLock);
        }
        catch
        {
            // The full-screen notification and turnScreenOn flag remain as fallbacks.
        }
    }

    private static async Task ReleaseWakeLockAsync(PowerManager.WakeLock wakeLock)
    {
        try
        {
            await Task.Delay(3000);
            if (wakeLock.IsHeld)
            {
                wakeLock.Release();
            }
        }
        catch
        {
            // The system can release a timed wake lock first.
        }
        finally
        {
            wakeLock.Dispose();
        }
    }

    private void PrepareLockScreenWindow()
    {
        if (Window is null)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(27))
            {
                SetShowWhenLocked(true);
                SetTurnScreenOn(true);
                return;
            }

#pragma warning disable CA1422 // Required on API 21-26.
            Window.AddFlags(
                WindowManagerFlags.ShowWhenLocked |
                WindowManagerFlags.TurnScreenOn |
                WindowManagerFlags.KeepScreenOn);
#pragma warning restore CA1422
        }
        catch
        {
            // Lock-screen presentation is best effort; the page itself still works.
        }
    }

    /// <summary>
    /// Android 13+ needs READ_MEDIA_AUDIO for the scanner and POST_NOTIFICATIONS for the
    /// lock-screen media controls; older versions use READ_EXTERNAL_STORAGE. Both are asked
    /// in one request because Android silently replaces a second in-flight request.
    /// </summary>
    private void RequestStartupPermissions()
    {
        // Runtime permissions only exist from Android 6 (API 23).
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return;
        }

        var pending = new List<string>();

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            const string readMediaAudio = "android.permission.READ_MEDIA_AUDIO";
            const string postNotifications = "android.permission.POST_NOTIFICATIONS";

            if (CheckSelfPermission(readMediaAudio) != Permission.Granted)
            {
                pending.Add(readMediaAudio);
            }

            if (CheckSelfPermission(postNotifications) != Permission.Granted)
            {
                pending.Add(postNotifications);
            }
        }
        else if (CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) != Permission.Granted)
        {
            pending.Add(Android.Manifest.Permission.ReadExternalStorage);
        }

        if (pending.Count > 0)
        {
            AndroidMediaAccess.BeginStartupRequest();
            RequestPermissions([.. pending], PermissionRequest);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Platform Compatibility",
        "CA1416",
        Justification = "MauiAppCompatActivity only invokes this on API 23+, where runtime permissions exist.")]
    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == PermissionRequest)
        {
            AndroidMediaAccess.CompleteStartupRequest();
        }
    }
}
