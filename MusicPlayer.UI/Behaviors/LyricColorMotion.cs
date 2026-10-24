using Microsoft.Maui.Controls;

namespace MusicPlayer.UI.Behaviors;

/// <summary>
/// Shared colour helpers for lyric motion. Only colour/opacity is animated here; changing
/// typography during playback would re-measure the row and make the auto-scroll jump.
/// </summary>
internal static class LyricColors
{
    public static Color Resource(string lightKey, string darkKey)
    {
        var app = Microsoft.Maui.Controls.Application.Current;
        var key = app?.RequestedTheme == AppTheme.Dark ? darkKey : lightKey;

        if (app?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return Colors.Transparent;
    }

    public static Color Lerp(Color from, Color to, float progress)
    {
        var amount = Math.Clamp(progress, 0f, 1f);

        return new Color(
            from.Red + ((to.Red - from.Red) * amount),
            from.Green + ((to.Green - from.Green) * amount),
            from.Blue + ((to.Blue - from.Blue) * amount),
            from.Alpha + ((to.Alpha - from.Alpha) * amount));
    }
}

/// <summary>Fades the soft pill behind the active lyric line in and out.</summary>
public static class LyricHighlightAnimation
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LyricHighlightAnimation),
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

        var active = (bool)newValue;
        var target = active
            ? LyricColors.Resource("AccentSoftLight", "AccentSoftDark")
            : Colors.Transparent;
        var start = border.BackgroundColor ?? Colors.Transparent;

        border.AbortAnimation("LyricHighlight");
        var animation = new Animation(
            progress => border.BackgroundColor = LyricColors.Lerp(start, target, (float)progress),
            0,
            1,
            Easing.CubicInOut);

        animation.Commit(
            border,
            "LyricHighlight",
            rate: 16,
            length: active ? 280u : 220u);
    }
}

/// <summary>
/// Cross-fades a lyric label between the muted and accent colours. Used by both whole lines
/// and karaoke words, so the highlight glides instead of switching instantly.
/// </summary>
public static class LyricTextColorAnimation
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.CreateAttached(
            "IsActive",
            typeof(bool),
            typeof(LyricTextColorAnimation),
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

        var active = (bool)newValue;
        var target = active
            ? LyricColors.Resource("Primary", "PrimaryDark")
            : LyricColors.Resource("MutedOnLight", "MutedOnDark");
        var start = label.TextColor ?? target;

        label.AbortAnimation("LyricTextColor");
        var animation = new Animation(
            progress => label.TextColor = LyricColors.Lerp(start, target, (float)progress),
            0,
            1,
            Easing.CubicOut);

        animation.Commit(
            label,
            "LyricTextColor",
            rate: 16,
            length: active ? 240u : 200u);
    }
}
