using Android.Content;
using Android.OS;
using Android.Provider;
using Uri = Android.Net.Uri;
using Environment = Android.OS.Environment;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Android implementation of <see cref="IStorageAccessService"/>.
///
/// Android 11+ only lets an app write files by path when the user enables "All files access"
/// (MANAGE_EXTERNAL_STORAGE). Music apps qualify for this permission. On Android 10 and
/// below the legacy read/write permissions are enough and this service reports access as
/// granted so the UI never nags.
/// </summary>
public sealed class AndroidStorageAccessService : IStorageAccessService
{
    public bool HasFullFileAccess
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                return true;
            }

            try
            {
                return Environment.IsExternalStorageManager;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task RequestFullFileAccessAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Older Android versions control access through the normal runtime permissions.
            return Task.CompletedTask;
        }

        var context = Android.App.Application.Context;

        try
        {
            // Per-app screen: the user sees a single switch for this app.
            var intent = new Intent(
                Settings.ActionManageAppAllFilesAccessPermission,
                Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch
        {
            try
            {
                // Fallback: the list of apps that may access all files.
#pragma warning disable CA1416 // The method already returned on Android < 30.
                var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
#pragma warning restore CA1416
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch
            {
                // Settings screen unavailable (rare vendor ROMs): nothing more we can do here.
            }
        }

        return Task.CompletedTask;
    }
}
