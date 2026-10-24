using System.ComponentModel;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class SearchPage : ContentPage
{
    private bool _introPlayed;
    private bool _subscribed;

    private SearchViewModel? ViewModel => BindingContext as SearchViewModel;

    public SearchPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<SearchViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!_subscribed && ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribed = true;
        }

        if (_introPlayed)
        {
            return;
        }

        _introPlayed = true;

        Motion.Prepare(HeaderBlock, offsetY: -10);
        Motion.Prepare(ResultsContent, offsetY: 16);

        await Task.Delay(16);
        await Motion.IntroAsync(0, 80, HeaderBlock, ResultsContent);
    }

    protected override void OnDisappearing()
    {
        if (_subscribed && ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed = false;
        }

        QueryEntry.Unfocus();
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Fade the new result set in when a search completes, so typing quickly does not
        // flash a hard cut between two sets of rows.
        if (e.PropertyName is nameof(SearchViewModel.HasSearched) or nameof(SearchViewModel.IsSearching))
        {
            if (ViewModel is { IsSearching: false })
            {
                _ = Motion.RefreshContentAsync(ResultsContent);
            }
        }
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        if (BindingContext is SearchViewModel viewModel)
        {
            viewModel.SearchQuery = string.Empty;
        }

        QueryEntry.Unfocus();
    }

    private async void OnNavLibraryClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async void OnNavSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(Routes.Settings);
    }
}
