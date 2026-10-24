using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class AddSongsPage : ContentPage, IQueryAttributable
{
    private int _playlistId;
    private bool _introPlayed;

    private AddSongsViewModel ViewModel => (AddSongsViewModel)BindingContext;

    public AddSongsPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<AddSongsViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("playlistId", out var value) &&
            int.TryParse(value?.ToString(), out var id))
        {
            _playlistId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_playlistId > 0)
        {
            ViewModel.SetPlaylistId(_playlistId);
        }

        ViewModel.LoadCommand.Execute(null);

        if (_introPlayed)
        {
            return;
        }

        _introPlayed = true;

        Motion.Prepare(HeaderBlock, offsetY: -8);
        Motion.Prepare(SearchBlock);
        Motion.Prepare(SongsList, offsetY: 20);

        await Task.Delay(16);

        await Motion.IntroAsync(0, 60, HeaderBlock, SearchBlock, SongsList);
    }

    private void OnSongTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: SelectableTrack item })
        {
            return;
        }

        Haptics.Tap();
        ViewModel.ToggleTrackCommand.Execute(item);

        if (sender is Grid row && row.FindByName<Border>("Check") is { } check)
        {
            _ = Motion.PulseAsync(check, 1.18);
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
