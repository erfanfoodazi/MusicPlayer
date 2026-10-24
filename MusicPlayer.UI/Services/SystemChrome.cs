#if ANDROID
using AndroidX.Core.View;
#endif

namespace MusicPlayer.UI.Services;

/// <summary>
/// Keeps Android's status/navigation bars in sync with the app's theme, and lets immersive
/// pages (Now Playing) switch them to a dark palette while they are visible.
/// </summary>
public static class SystemChrome
{
    public static void ApplyAppTheme()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window is not { } window)
        {
            return;
        }

        var night = (activity.Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)
                    == Android.Content.Res.UiMode.NightYes;
        Apply(window, night ? "#08080C" : "#F7F7FA", !night);
#endif
    }

    /// <summary>Keeps the system bars dark while the immersive lock-screen player is open.</summary>
    public static void ApplyLockScreen()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window is { } window)
        {
            Apply(window, "#08080C", false);
        }
#endif
    }

#if ANDROID
    private static void Apply(Android.Views.Window window, string colorHex, bool lightIcons)
    {
        var color = Android.Graphics.Color.ParseColor(colorHex);
#pragma warning disable CA1422 // Status/navigation bar colours are deprecated in API 35 but still honoured.
        window.SetStatusBarColor(color);
        window.SetNavigationBarColor(color);
#pragma warning restore CA1422

        try
        {
            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller is null)
            {
                return;
            }

            controller.AppearanceLightStatusBars = lightIcons;
            controller.AppearanceLightNavigationBars = lightIcons;
        }
        catch
        {
            // System-bar styling is cosmetic; never let it take a page down.
        }
    }
#endif
}
