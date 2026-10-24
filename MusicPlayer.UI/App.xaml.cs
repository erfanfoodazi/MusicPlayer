using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();

        // Palette resources must be in place before the first Shell/page is built.
        ThemeService.Apply();

        var theme = Preferences.Default.Get("settings.theme", "System");
        UserAppTheme = theme switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}
