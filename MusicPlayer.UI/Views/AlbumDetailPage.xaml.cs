using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class AlbumDetailPage : ContentPage, IQueryAttributable
{
    private int _albumId;
    private bool _introPlayed;

    public AlbumDetailPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<AlbumDetailViewModel>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("albumId", out var value) &&
            int.TryParse(value?.ToString(), out var id))
        {
            _albumId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is AlbumDetailViewModel viewModel && _albumId > 0)
        {
            viewModel.SetAlbumId(_albumId);
            await viewModel.LoadAlbumCommand.ExecuteAsync(null);

            if (!_introPlayed)
            {
                _introPlayed = true;
                await RunIntroAsync();
            }
        }
    }

    private async Task RunIntroAsync()
    {
        Motion.Prepare(HeaderBlock, offsetY: -10);
        Motion.Prepare(TracksList, offsetY: 18);

        await Task.Delay(16);
        await Motion.IntroAsync(initialDelay: 10, step: 80, HeaderBlock, TracksList);
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
