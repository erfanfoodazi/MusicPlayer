#if ANDROID
using AndroidX.Core.View;
#endif

namespace MusicPlayer.UI.Services;

/// <summary>
/// Keeps Android's status/navigation bars in sync with the app's theme, and lets the player
/// pages paint them with the same cover colour as the page behind them so there is no seam
/// under the status bar.
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
        var background = ThemeBarColor(night);

        // Every tab page opens with the accent header wash, so the status bar carries the same
        // tint instead of sitting as a flat strip above it. The navigation bar continues the
        // tab bar's own background.
        var status = AccentTint(background);
        Apply(window, status, background, !night);

        // Android 15 ignores the status/navigation bar colour and makes both transparent.
        // Painting the window itself is what fills those strips with the app colour.
        ApplyWindowBackdrop(window, status, background);
#endif
    }

#if ANDROID
    private static Android.Graphics.Color AccentTint(Android.Graphics.Color background)
    {
        if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue("Primary", out var value) != true ||
            value is not Color accent)
        {
            return background;
        }

        // Matches the 16% accent stop at the top of HeaderWash.
        const double alpha = 0.16;

        return Android.Graphics.Color.Argb(
            255,
            (int)Math.Round(background.R + ((accent.Red * 255) - background.R) * alpha),
            (int)Math.Round(background.G + ((accent.Green * 255) - background.G) * alpha),
            (int)Math.Round(background.B + ((accent.Blue * 255) - background.B) * alpha));
    }
#endif

#if ANDROID
    /// <summary>
    /// The tab bar reads the same resources, so the page and the system bars stay one surface
    /// instead of the navigation strip drifting to a different shade.
    /// </summary>
    private static Android.Graphics.Color ThemeBarColor(bool night)
    {
        var key = night ? "AppBackgroundDark" : "AppBackgroundLight";

        if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true &&
            value is Color color)
        {
            return ToAndroidColor(color);
        }

        return Android.Graphics.Color.ParseColor(night ? "#07070C" : "#F4F4F9");
    }
#endif

    /// <summary>Keeps the system bars dark while the immersive lock-screen player is open.</summary>
    public static void ApplyLockScreen()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window is not { } window)
        {
            return;
        }

        var color = Android.Graphics.Color.ParseColor("#07070C");
        Apply(window, color, false);
        ApplyWindowBackdrop(window, color, color);

        if (TryGetTopDialogWindow() is { } dialogWindow)
        {
            Apply(dialogWindow, color, false);
            ApplyWindowBackdrop(dialogWindow, color, color);
        }
#endif
    }

    /// <summary>
    /// Paints the system bars with the player's own colours: the status bar continues the top
    /// of the artwork wash and the navigation bar continues its bottom edge.
    /// </summary>
    public static void ApplyPlayerChrome(Color statusBarColor, Color navigationBarColor)
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.Window is not { } window)
        {
            return;
        }

        var status = ToAndroidColor(statusBarColor);
        var navigation = ToAndroidColor(navigationBarColor);
        var lightIcons = IsLightColor(statusBarColor);
        Apply(window, status, navigation, lightIcons);

        // MAUI presents modal pages in a DialogFragment with its own window, and that window
        // owns the strips while the player is open. The base window is painted as well so the
        // strips are already right the moment the modal closes.
        if (TryGetTopDialogWindow() is { } dialogWindow)
        {
            Apply(dialogWindow, status, navigation, lightIcons);
            ApplyWindowBackdrop(dialogWindow, status, navigation);
        }

        ApplyWindowBackdrop(window, status, navigation);
#endif
    }

#if ANDROID
    /// <summary>
    /// The window of the topmost visible MAUI modal (its DialogFragment). Returns null when no
    /// dialog is showing, which is the normal case on the tab pages.
    /// </summary>
    private static Android.Views.Window? TryGetTopDialogWindow()
    {
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                is not AndroidX.Fragment.App.FragmentActivity activity)
            {
                return null;
            }

            Android.Views.Window? top = null;

            void Walk(AndroidX.Fragment.App.FragmentManager manager)
            {
                try
                {
                    foreach (var fragment in manager.Fragments)
                    {
                        if (fragment is AndroidX.Fragment.App.DialogFragment dialogFragment &&
                            dialogFragment.Dialog is { IsShowing: true } dialog)
                        {
                            top = dialog.Window ?? top;
                        }

                        if (fragment.ChildFragmentManager is { } children)
                        {
                            Walk(children);
                        }
                    }
                }
                catch
                {
                    // A fragment can disappear between enumeration and inspection.
                }
            }

            Walk(activity.SupportFragmentManager);
            return top;
        }
        catch
        {
            return null;
        }
    }

#endif

#if ANDROID
    /// <summary>
    /// Fills the strips Android 15 leaves transparent above and below the page. A vertical
    /// gradient keeps the top strip matching the artwork wash and the bottom strip matching
    /// the bottom of the page instead of showing the window's black default.
    /// </summary>
    private static void ApplyWindowBackdrop(
        Android.Views.Window window,
        Android.Graphics.Color top,
        Android.Graphics.Color bottom)
    {
        try
        {
            var decor = window.DecorView;
            if (decor is null)
            {
                return;
            }

            decor.Background = new Android.Graphics.Drawables.GradientDrawable(
                Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom,
                [top.ToArgb(), bottom.ToArgb()]);
            decor.Invalidate();
            decor.PostInvalidateOnAnimation();
        }
        catch
        {
            // The window may not have a decor view yet.
        }
    }

    private static Android.Graphics.Color ToAndroidColor(Color color) =>
        Android.Graphics.Color.Argb(
            255,
            (int)Math.Round(Math.Clamp(color.Red, 0f, 1f) * 255),
            (int)Math.Round(Math.Clamp(color.Green, 0f, 1f) * 255),
            (int)Math.Round(Math.Clamp(color.Blue, 0f, 1f) * 255));

    private static bool IsLightColor(Color color) =>
        (color.Red * 0.299) + (color.Green * 0.587) + (color.Blue * 0.114) > 0.62;

    /// <summary>
    /// Turns off the skin's automatic contrast scrim behind the system bars. The .NET binding
    /// for these API 29 methods is missing, so they are called through the Java window.
    /// </summary>
    private static void DisableBarContrast(Android.Views.Window window)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return;
        }

        try
        {
            using var disabled = new Java.Lang.Boolean(false);
            var args = new Java.Lang.Object?[] { disabled };

            foreach (var name in new[] { "setStatusBarContrastEnforced", "setNavigationBarContrastEnforced" })
            {
                using var method = window.Class?.GetMethod(name, Java.Lang.Boolean.Type);
                method?.Invoke(window, args);
            }
        }
        catch
        {
            // The system bar colour above remains correct even if the scrim cannot be turned off.
        }
    }

    private static void Apply(Android.Views.Window window, Android.Graphics.Color color, bool lightIcons) =>
        Apply(window, color, color, lightIcons);

    private static void Apply(
        Android.Views.Window window,
        Android.Graphics.Color statusBarColor,
        Android.Graphics.Color navigationBarColor,
        bool lightIcons)
    {
#pragma warning disable CA1422 // Status/navigation bar colours are deprecated in API 35 but still honoured.
        try
        {
            window.SetStatusBarColor(statusBarColor);
            window.SetNavigationBarColor(navigationBarColor);
        }
        catch
        {
            // Deprecated and enforced edge-to-edge on Android 15: the decor paint below is the
            // real fill, so a rejected bar colour must not skip it.
        }

        // Samsung and other skins otherwise lay a contrast scrim over the navigation bar,
        // which reads as a different shade from the tab bar painted directly above it.
        DisableBarContrast(window);
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
