using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class LyricsEditorPage : ContentPage
{
    private bool _introPlayed;

    private LyricsEditorViewModel ViewModel => (LyricsEditorViewModel)BindingContext;

    public LyricsEditorPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<LyricsEditorViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ViewModel.Load();

        if (_introPlayed)
        {
            return;
        }

        _introPlayed = true;

        Motion.Prepare(TopBar, offsetY: -8);
        Motion.Prepare(TrackBlock);
        Motion.Prepare(EditorCard, offsetY: 16);

        await Task.Delay(16);
        await Motion.IntroAsync(0, 60, TopBar, TrackBlock, EditorCard);
    }
}
