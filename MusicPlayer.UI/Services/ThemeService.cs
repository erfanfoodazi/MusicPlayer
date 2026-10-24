using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace MusicPlayer.UI.Services;

/// <summary>One selectable accent palette. Light/dark variants are both supplied.</summary>
public sealed record AccentPalette(
    string Id,
    string Name,
    string Primary,
    string PrimaryDark,
    string SoftLight,
    string SoftDark,
    string GradientStart,
    string GradientMiddle,
    string GradientEnd);

/// <summary>
/// Applies the user's accent palette to the app resource dictionary. MAUI resolves
/// StaticResource once, so a palette change rebuilds the Shell after the resources have been
/// swapped; playback lives in the PlayerService singleton and continues uninterrupted.
/// </summary>
public static class ThemeService
{
    private const string AccentPreferenceKey = "settings.accent";

    public static IReadOnlyList<AccentPalette> Palettes { get; } =
    [
        new("Violet", "Violet", "#6D5BF6", "#B7A7FF", "#F0ECFF", "#1A1533", "#6D5BF6", "#8B5CF6", "#C084FC"),
        new("Rose", "Rose", "#D93B6C", "#FF8FB1", "#FFE9F0", "#2A111B", "#E84A78", "#F06E9A", "#FFA6C4"),
        new("Ocean", "Ocean", "#0B6BCB", "#67B7FF", "#E7F1FF", "#0B1B2F", "#1473E6", "#248DEE", "#45C4F5"),
        new("Emerald", "Emerald", "#0B8F6A", "#57E0B4", "#E4F8F0", "#0A241C", "#0F9D78", "#1FBF91", "#4AE3B4"),
        new("Sunset", "Sunset", "#E85D04", "#FFA65C", "#FFF0E3", "#2A160A", "#F97316", "#FB8A56", "#FFB36B"),
        new("Graphite", "Graphite", "#4B5563", "#D4D4D8", "#EFEFF3", "#1B1B22", "#52525B", "#71717A", "#A1A1AA"),
    ];

    public static string CurrentId => Preferences.Default.Get(AccentPreferenceKey, "Violet");

    public static AccentPalette Current =>
        Palettes.FirstOrDefault(palette => string.Equals(palette.Id, CurrentId, StringComparison.OrdinalIgnoreCase))
        ?? Palettes[0];

    public static void Apply(string? id = null)
    {
        var palette = Palettes.FirstOrDefault(p =>
                          string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? Current;

        Preferences.Default.Set(AccentPreferenceKey, palette.Id);

        var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var primary = Color.FromArgb(palette.Primary);
        var primaryDark = Color.FromArgb(palette.PrimaryDark);

        resources["Primary"] = primary;
        resources["PrimaryDark"] = primaryDark;
        resources["Accent"] = primary;
        resources["AccentSoftLight"] = Color.FromArgb(palette.SoftLight);
        resources["AccentSoftDark"] = Color.FromArgb(palette.SoftDark);
        resources["GradientStart"] = Color.FromArgb(palette.GradientStart);
        resources["GradientMiddle"] = Color.FromArgb(palette.GradientMiddle);
        resources["GradientEnd"] = Color.FromArgb(palette.GradientEnd);
        resources["PrimaryBrush"] = new SolidColorBrush(primary);

        var accentGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(palette.GradientStart), 0),
                new(Color.FromArgb(palette.GradientMiddle), 0.55f),
                new(Color.FromArgb(palette.GradientEnd), 1),
            }
        };
        resources["AccentGradient"] = accentGradient;

        var horizontalGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(palette.GradientStart), 0),
                new(Color.FromArgb(palette.GradientEnd), 1),
            }
        };
        resources["AccentGradientHorizontal"] = horizontalGradient;

        var headerWash = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new(primary.WithAlpha(0.16f), 0),
                new(primary.WithAlpha(0.05f), 0.55f),
                new(primary.WithAlpha(0f), 1),
            }
        };
        resources["HeaderWash"] = headerWash;
    }

    /// <summary>Rebuilds the visual tree so every StaticResource picks up the new palette.</summary>
    public static void ReloadShell()
    {
        var application = Microsoft.Maui.Controls.Application.Current;
        if (application?.Windows.FirstOrDefault() is not { } window)
        {
            return;
        }

        window.Page = new AppShell();
    }
}
