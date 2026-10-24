using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class AlbumDetailPage : ContentPage, IQueryAttributable
{
    private int _albumId;
    private bool _introPlayed;
    private CancellationTokenSource? _backdropCancellation;

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
            await ApplyBackdropAsync(viewModel.AlbumArtworkPath);

            if (!_introPlayed)
            {
                _introPlayed = true;
                await RunIntroAsync();
            }
        }
    }

    private async Task RunIntroAsync()
    {
        Motion.Prepare(PageHeader, offsetY: -8);
        Motion.Prepare(AlbumContent, offsetY: 20);

        try
        {
            AmbientBackdrop.CancelAnimations();
            AmbientBackdrop.Opacity = 0.35f;
            _ = AmbientBackdrop.FadeTo(1, 420, Easing.CubicOut);
        }
        catch
        {
            AmbientBackdrop.Opacity = 1;
        }

        await Task.Delay(16);
        await Motion.IntroAsync(0, 80, PageHeader, AlbumContent);
    }

    protected override void OnDisappearing()
    {
        _backdropCancellation?.Cancel();
        base.OnDisappearing();
    }

    /// <summary>
    /// Tints the upper half of the page with the sleeve's dominant colour. The brush fades
    /// into the normal page background, so rows and the dock keep the same contrast as before.
    /// </summary>
    private async Task ApplyBackdropAsync(string? artworkPath)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _backdropCancellation, cancellation);
        previous?.Cancel();

        Color? accent;
        try
        {
            accent = await ArtworkColors.GetDominantColorAsync(artworkPath, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            accent = null;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        var dark = (Microsoft.Maui.Controls.Application.Current?.RequestedTheme ?? AppTheme.Dark) != AppTheme.Light;
        AmbientBackdrop.Background = ArtworkColors.CreateBackdropBrush(accent, dark);
        BackgroundColor = ArtworkColors.BackdropBase(dark);
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
