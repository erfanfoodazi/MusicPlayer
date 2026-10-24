using Microsoft.Maui.Controls;

namespace MusicPlayer.UI.Behaviors;

/// <summary>
/// The lock-screen player is always dark, so its lyric highlight uses a fixed palette
/// instead of the app's theme-aware colours. Only colour is animated, never typography.
/// </summary>
public static class LockScreenLyricTextColor
{
    private static readonly Color Inactive = Color.FromArgb("#B7B7C8");
    private static readonly Color Active = Color.FromArgb("#C7B8FF");

    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LockScreenLyricTextColor),
            false,
            propertyChanged: OnIsActiveChanged);

    public static bool GetIsActive(BindableObject view) => (bool)view.GetValue(IsActiveProperty);

    public static void SetIsActive(BindableObject view, bool value) => view.SetValue(IsActiveProperty, value);

    private static void OnIsActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Label label)
        {
            return;
        }

        var isActive = (bool)newValue;
        var target = isActive ? Active : Inactive;
        var start = label.TextColor ?? target;

        label.AbortAnimation("LockScreenLyricText");
        new Animation(
                progress => label.TextColor = LyricColors.Lerp(start, target, (float)progress),
                0,
                1,
                Easing.CubicOut)
            .Commit(
                label,
                "LockScreenLyricText",
                rate: 16,
                length: isActive ? 250u : 190u);
    }
}

/// <summary>Soft violet pill behind the current lyric line on the lock-screen player.</summary>
public static class LockScreenLyricHighlight
{
    private static readonly Color Active = Color.FromArgb("#352B52");

    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LockScreenLyricHighlight),
            false,
            propertyChanged: OnIsActiveChanged);

    public static bool GetIsActive(BindableObject view) => (bool)view.GetValue(IsActiveProperty);

    public static void SetIsActive(BindableObject view, bool value) => view.SetValue(IsActiveProperty, value);

    private static void OnIsActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Border border)
        {
            return;
        }

        var isActive = (bool)newValue;
        var target = isActive ? Active : Colors.Transparent;
        var start = border.BackgroundColor ?? Colors.Transparent;

        border.AbortAnimation("LockScreenLyricHighlight");
        new Animation(
                progress => border.BackgroundColor = LyricColors.Lerp(start, target, (float)progress),
                0,
                1,
                Easing.CubicInOut)
            .Commit(
                border,
                "LockScreenLyricHighlight",
                rate: 16,
                length: isActive ? 280u : 210u);
    }
}
