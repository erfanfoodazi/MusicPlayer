using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class ArtistDetailPage : ContentPage, IQueryAttributable
{
    private int _artistId;
    private bool _introPlayed;

    public ArtistDetailPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<ArtistDetailViewModel>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("artistId", out var value) &&
            int.TryParse(value?.ToString(), out var id))
        {
            _artistId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is ArtistDetailViewModel viewModel && _artistId > 0)
        {
            viewModel.SetArtistId(_artistId);
            await viewModel.LoadArtistCommand.ExecuteAsync(null);

            if (!_introPlayed)
            {
                _introPlayed = true;

                Motion.Prepare(PageHeader, offsetY: -8);
                Motion.Prepare(ArtistContent, offsetY: 20);

                await Task.Delay(16);
                await Motion.IntroAsync(0, 80, PageHeader, ArtistContent);
            }
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
