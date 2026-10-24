using Microsoft.Maui.Controls;

namespace MusicPlayer.UI.Behaviors;

/// <summary>
/// Tiny lift when a karaoke word becomes active. The color change is handled by a data
/// trigger; this only adds the motion so enhanced LRC lines feel alive without an
/// expensive per-word timer.
/// </summary>
public static class LyricWordAnimation
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LyricWordAnimation),
            false,
            propertyChanged: OnIsActiveChanged);

    public static bool GetIsActive(BindableObject view) => (bool)view.GetValue(IsActiveProperty);

    public static void SetIsActive(BindableObject view, bool value) => view.SetValue(IsActiveProperty, value);

    private static async void OnIsActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Label label)
        {
            return;
        }

        try
        {
            label.CancelAnimations();

            if ((bool)newValue)
            {
                label.Opacity = 0.62;
                label.TranslationY = 1;

                await Task.WhenAll(
                    label.FadeTo(1, 130, Easing.CubicOut),
                    label.TranslateTo(0, 0, 150, Easing.CubicOut));
            }
            else
            {
                label.Opacity = 1;
                label.TranslationY = 0;
            }
        }
        catch
        {
            // The row may be recycled while the animation is running.
        }
    }
}
