using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class PlaylistDetailPage : ContentPage, IQueryAttributable
{
    private int _playlistId;
    private bool _introPlayed;

    public PlaylistDetailPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<PlaylistDetailViewModel>();
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

        if (BindingContext is PlaylistDetailViewModel viewModel && _playlistId > 0)
        {
            viewModel.SetPlaylistId(_playlistId);
            await viewModel.LoadPlaylistCommand.ExecuteAsync(null);

            if (!_introPlayed)
            {
                _introPlayed = true;

                Motion.Prepare(TopBar, offsetY: -8);
                Motion.Prepare(PlaylistContent, offsetY: 20);

                await Task.Delay(16);
                await Motion.IntroAsync(0, 70, TopBar, PlaylistContent);
            }
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
