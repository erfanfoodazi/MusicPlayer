#if ANDROID
using Android.Views;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Lets the full-screen player sit on top of the Android keyguard. This is intentionally
/// enabled only while the lock-screen player page is open; normal app screens keep the
/// platform's default keyguard protection.
/// </summary>
public static class LockScreenMode
{
    public static void Enable() => SetShowWhenLocked(true);

    public static void Disable() => SetShowWhenLocked(false);

    private static void SetShowWhenLocked(bool enabled)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            activity.SetShowWhenLocked(enabled);
            return;
        }

#pragma warning disable CA1422 // Required on API 21-26.
        if (enabled)
        {
            activity.Window.AddFlags(WindowManagerFlags.ShowWhenLocked);
        }
        else
        {
            activity.Window.ClearFlags(WindowManagerFlags.ShowWhenLocked);
        }
#pragma warning restore CA1422
    }
}
#endif
