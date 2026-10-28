using System.ComponentModel;
using Domain.Models;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class LibraryPage : ContentPage
{
    private bool _subscribed;
    private bool _introPlayed;
    private int _lastTabIndex = -1;

    public LibraryPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<LibraryViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is LibraryViewModel viewModel)
        {
            if (!_subscribed)
            {
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _lastTabIndex = viewModel.SelectedTabIndex;
                _subscribed = true;
            }

            await viewModel.LoadDataCommand.ExecuteAsync(null);

            if (!_introPlayed)
            {
                _introPlayed = true;
                await RunIntroAsync(viewModel.SelectedTabIndex);
            }
            else
            {
                await Motion.ReenterAsync(HeaderBlock, GetTabView(viewModel.SelectedTabIndex));
            }
        }
    }

    /// <summary>
    /// First-run page intro: the header settles first, then the active list. The page is
    /// already painted, so this is a compositor-only reveal with no layout work.
    /// </summary>
    private async Task RunIntroAsync(int index)
    {
        var content = GetTabView(index);

        Motion.Prepare(HeaderBlock, offsetY: -10);
        Motion.Prepare(content, offsetY: 18);

        await Task.Delay(16);

        await Motion.IntroAsync(initialDelay: 10, step: 80, HeaderBlock, content);
    }

    private VisualElement? GetTabView(int index) => index switch
    {
        0 => TracksView,
        1 => AlbumsView,
        2 => ArtistsView,
        3 => PlaylistsView,
        _ => null
    };

    protected override void OnDisappearing()
    {
        if (_subscribed && BindingContext is LibraryViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed = false;
        }

        // Leaving the page with the search entry focused keeps Android's keyboard open,
        // which resizes the window and leaves a blank strip under the next page.
        SearchEntry.Unfocus();
        base.OnDisappearing();
    }

    /// <summary>Shelves play immediately; the full track list keeps its context queue.</summary>
    private void OnRecentTrackTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is not Music track ||
            BindingContext is not LibraryViewModel viewModel)
        {
            return;
        }

        Haptics.Tap();
        viewModel.PlayTrackCommand.Execute(track);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.SelectedTabIndex) ||
            BindingContext is not LibraryViewModel viewModel)
        {
            return;
        }

        var index = viewModel.SelectedTabIndex;
        if (index == _lastTabIndex)
        {
            return;
        }

        _lastTabIndex = index;

        if (!_introPlayed)
        {
            return;
        }

        Haptics.Tap();

        // Let the IsVisible binding settle for one frame before animating the incoming tab.
        Dispatcher.DispatchDelayed(
            TimeSpan.FromMilliseconds(16),
            () => _ = AnimateTabInAsync(index));
    }

    private async Task AnimateTabInAsync(int index)
    {
        var view = GetTabView(index);

        if (view is null || !view.IsVisible)
        {
            return;
        }

        try
        {
            view.CancelAnimations();
            view.Opacity = 0.2;
            view.TranslationY = 12;
            view.Scale = 0.994;

            await Task.WhenAll(
                view.FadeTo(1, 190, Easing.CubicOut),
                view.TranslateTo(0, 0, 230, Easing.CubicOut),
                view.ScaleTo(1, 230, Easing.CubicOut));
        }
        catch
        {
            // Tabs can be switched again while the previous animation is still running.
        }
    }
}
