using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class TagEditorPage : ContentPage
{
    private bool _introPlayed;

    private TagEditorViewModel ViewModel => (TagEditorViewModel)BindingContext;

    public TagEditorPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<TagEditorViewModel>();
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
        Motion.Prepare(CoverSection);
        Motion.Prepare(TrackSection);
        Motion.Prepare(NumberingSection);
        Motion.Prepare(GenresSection);

        await Task.Delay(16);

        await Motion.IntroAsync(
            initialDelay: 0,
            step: 55,
            TopBar,
            CoverSection,
            TrackSection,
            NumberingSection,
            GenresSection);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
