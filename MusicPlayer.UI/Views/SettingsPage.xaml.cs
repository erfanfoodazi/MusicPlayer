using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class SettingsPage : ContentPage
{
    private bool _introPlayed;

    private SettingsViewModel ViewModel => (SettingsViewModel)BindingContext;

    public SettingsPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<SettingsViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // The user may have just granted (or revoked) all-files access in the system screen.
        ViewModel.RefreshStorageAccess();

        if (_introPlayed)
        {
            return;
        }

        _introPlayed = true;

        Motion.Prepare(HeaderBlock, offsetY: -8);
        Motion.Prepare(PlaybackSection);
        Motion.Prepare(LibrarySection);
        Motion.Prepare(AppearanceSection);
        Motion.Prepare(StorageSection);
        Motion.Prepare(FileAccessSection);
        Motion.Prepare(AdvancedSection);

        await Task.Delay(16);

        await Motion.IntroAsync(
            initialDelay: 0,
            step: 55,
            HeaderBlock,
            PlaybackSection,
            LibrarySection,
            AppearanceSection,
            StorageSection,
            FileAccessSection,
            AdvancedSection);
    }

    private async void OnNavLibraryClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async void OnNavSearchClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(Routes.Search);
    }
}
