using Microsoft.Maui.Controls;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Shared, compositor-only motion used across the app. Every page uses the same rhythm -
/// a short fade plus a small upward travel - so navigation feels like one product instead
/// of a collection of screens. Only opacity, translation and scale are touched, so no
/// animation forces a layout pass.
/// </summary>
public static class Motion
{
    public const uint Fast = 160;
    public const uint Normal = 250;
    public const uint Slow = 360;

    /// <summary>
    /// Staggers a set of sections into view. Sections that are hidden are skipped, and an
    /// animation cancelled by a fast navigation never throws into the page.
    /// </summary>
    public static async Task IntroAsync(params VisualElement?[] sections)
        => await IntroAsync(initialDelay: 20, step: 65, sections);

    public static async Task IntroAsync(int initialDelay, int step, params VisualElement?[] sections)
    {
        if (sections.Length == 0)
        {
            return;
        }

        var animations = new List<Task>(sections.Length);

        for (var i = 0; i < sections.Length; i++)
        {
            var element = sections[i];
            if (element is null)
            {
                continue;
            }

            animations.Add(AnimateSectionAsync(element, initialDelay + (i * step)));
        }

        await Task.WhenAll(animations);
    }

    private static async Task AnimateSectionAsync(VisualElement element, int delay)
    {
        try
        {
            // Capture the target state before hiding so a hidden section can still settle
            // correctly if the page appears and disappears quickly.
            if (delay > 0)
            {
                await Task.Delay(delay);
            }

            if (!element.IsVisible)
            {
                // The section was hidden between Prepare and its turn. Leave it in a
                // settled state so it is visible the moment it is shown again.
                element.Opacity = 1;
                element.TranslationY = 0;
                element.Scale = 1;
                return;
            }

            await Task.WhenAll(
                element.FadeTo(1, Normal, Easing.CubicOut),
                element.TranslateTo(0, 0, Normal + 60, Easing.CubicOut),
                element.ScaleTo(1, Normal + 60, Easing.CubicOut));
        }
        catch
        {
            // A page dismissed mid-animation is expected; make sure it is still visible.
            element.Opacity = 1;
            element.TranslationY = 0;
            element.Scale = 1;
        }
    }

    /// <summary>Puts an element into its pre-intro state. Call from the page constructor or first appear.</summary>
    public static void Prepare(VisualElement? element, double offsetY = 16, double scale = 1)
    {
        if (element is null)
        {
            return;
        }

        element.CancelAnimations();
        element.Opacity = 0;
        element.TranslationY = offsetY;
        element.Scale = scale;
    }

    public static void PrepareAll(params VisualElement?[] elements)
    {
        foreach (var element in elements)
        {
            Prepare(element);
        }
    }

    /// <summary>Forces every listed element into its final, visible state.</summary>
    public static void Settle(params VisualElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null)
            {
                continue;
            }

            element.CancelAnimations();
            element.Opacity = 1;
            element.TranslationY = 0;
            element.Scale = 1;
        }
    }

    /// <summary>Small pop used when a value or state changes.</summary>
    public static async Task PulseAsync(VisualElement? element, double scale = 1.06)
    {
        if (element is null || !element.IsVisible)
        {
            return;
        }

        try
        {
            element.CancelAnimations();
            await element.ScaleTo(scale, 120, Easing.CubicOut);
            await element.ScaleTo(1, 170, Easing.SpringOut);
        }
        catch
        {
            element.Scale = 1;
        }
    }

    /// <summary>Soft fade/slide used when a list of results is replaced.</summary>
    public static async Task RefreshContentAsync(VisualElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return;
        }

        try
        {
            element.CancelAnimations();
            element.Opacity = 0.35;
            element.TranslationY = 6;
            await Task.WhenAll(
                element.FadeTo(1, Fast, Easing.CubicOut),
                element.TranslateTo(0, 0, Normal, Easing.CubicOut));
        }
        catch
        {
            element.Opacity = 1;
            element.TranslationY = 0;
        }
    }

    /// <summary>
    /// Quick tactile press used by list rows: a small dip to 97% that springs back. Safe to
    /// fire and forget next to navigation - it never blocks the tap's action.
    /// </summary>
    public static async Task PressAsync(VisualElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return;
        }

        try
        {
            element.CancelAnimations();
            await element.ScaleTo(0.97, 70, Easing.CubicOut);
            await element.ScaleTo(1, 130, Easing.CubicOut);
        }
        catch
        {
            element.Scale = 1;
        }
    }

    /// <summary>Single-shot reveal for a page section that was just made visible.</summary>
    public static async Task RevealAsync(VisualElement? element, double offsetY = 10)
    {
        if (element is null)
        {
            return;
        }

        Prepare(element, offsetY);
        await AnimateSectionAsync(element, 16);
    }
}
