using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();

        var theme = Preferences.Default.Get("settings.theme", "System");
        UserAppTheme = theme switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };

        // Palette resources must be in place before the first Shell/page is built. Apply after
        // UserAppTheme so the light/dark accent variant is chosen correctly.
        ThemeService.Apply();

        RequestedThemeChanged += (_, _) =>
        {
            // Theme events can arrive while Android is reconfiguring the activity. Update the
            // resource keys on the next main-thread turn instead of re-entering layout code.
            Dispatcher.Dispatch(ThemeService.RefreshThemeColors);
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}
