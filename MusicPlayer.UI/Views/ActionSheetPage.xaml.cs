using MusicPlayer.UI.Services;
using Microsoft.Maui.Controls.Shapes;

namespace MusicPlayer.UI.Views;

public partial class ActionSheetPage : ContentPage
{
    private readonly TaskCompletionSource<int> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closing;

    public Task<int> Result => _result.Task;

    public ActionSheetPage(string title, string? subtitle, IReadOnlyList<AppSheetAction> actions)
    {
        InitializeComponent();

        NavigationPage.SetHasNavigationBar(this, false);
        TitleLabel.Text = title;
        SubtitleLabel.Text = subtitle ?? string.Empty;
        SubtitleLabel.IsVisible = !string.IsNullOrWhiteSpace(subtitle);
        BindableLayout.SetItemsSource(ActionsLayout, actions);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Fade/scale in place. Translating from off-screen used to leave the sheet visibly
        // half-open on slower devices (and on tall sheets).
        Sheet.Opacity = 0;
        Sheet.Scale = 0.97;
        await Task.WhenAll(
            Sheet.FadeTo(1, 150, Easing.CubicOut),
            Sheet.ScaleTo(1, 190, Easing.CubicOut));
    }

    private async void OnActionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bindable ||
            bindable.BindingContext is not AppSheetAction action)
        {
            return;
        }

        Haptics.Tap();
        await CloseAsync(action.Index);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(-1);

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(-1);

    private async Task CloseAsync(int result)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            await Task.WhenAll(
                Sheet.FadeTo(0, 120, Easing.CubicIn),
                Sheet.ScaleTo(0.98, 120, Easing.CubicIn));
        }
        catch
        {
            // The page may already be disappearing.
        }

        _result.TrySetResult(result);
        try
        {
            await Navigation.PopModalAsync(false);
        }
        catch
        {
            // Known MAUI Shell modal issue; ignore and let the app continue.
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(-1);
        return true;
    }
}
