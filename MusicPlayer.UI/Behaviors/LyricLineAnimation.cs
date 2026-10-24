using Microsoft.Maui.Controls;

namespace MusicPlayer.UI.Behaviors;

/// <summary>
/// Attached animation for a synced lyric line. Attached properties inherit the row's binding
/// context; a <see cref="Behavior{T}"/> inside a DataTemplate does not, which is why the
/// previous version never animated. Lines cross-fade while the list scrolls and the colour
/// motion handles the highlight; no size/position changes are made to the row.
/// </summary>
public static class LyricLineAnimation
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LyricLineAnimation),
            false,
            propertyChanged: OnIsActiveChanged);

    public static bool GetIsActive(BindableObject view) => (bool)view.GetValue(IsActiveProperty);

    public static void SetIsActive(BindableObject view, bool value) => view.SetValue(IsActiveProperty, value);

    private static async void OnIsActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not VisualElement element)
        {
            return;
        }

        var active = (bool)newValue;

        try
        {
            element.CancelAnimations();

            if (active)
            {
                element.Opacity = 0.68;
                await element.FadeTo(1, 280, Easing.CubicOut);
            }
            else
            {
                await element.FadeTo(0.42, 220, Easing.CubicOut);
            }
        }
        catch
        {
            // Animations are cancelled when a virtualized row is recycled or the page closes.
        }
    }
}
