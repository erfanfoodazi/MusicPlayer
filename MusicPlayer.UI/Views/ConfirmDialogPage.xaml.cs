using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Views;

public partial class ConfirmDialogPage : ContentPage
{
    private readonly TaskCompletionSource<bool> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closing;

    public Task<bool> Result => _result.Task;

    public ConfirmDialogPage(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool destructive,
        bool showCancel = true)
    {
        InitializeComponent();

        NavigationPage.SetHasNavigationBar(this, false);
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        ConfirmButton.Text = confirmText;
        CancelButton.Text = cancelText;
        CancelButton.IsVisible = showCancel;

        if (!destructive)
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app is not null)
            {
                ConfirmButton.Style = (Style)app.Resources["PrimaryPillButton"];
                IconGlyphLabel.Text = "\ue88e";
                IconGlyphLabel.TextColor = (Color)app.Resources["PrimaryDark"];
            }

            ConfirmButton.CornerRadius = 16;
            IconTile.BackgroundColor = Color.FromArgb("#241C3F");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        Card.Opacity = 0;
        Card.Scale = 0.92;
        await Task.WhenAll(
            Card.FadeTo(1, 170, Easing.CubicOut),
            Card.ScaleTo(1, 220, Easing.CubicOut));
    }

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        await CloseAsync(true);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(false);

    private async Task CloseAsync(bool confirmed)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            await Task.WhenAll(
                Card.FadeTo(0, 120, Easing.CubicIn),
                Card.ScaleTo(0.96, 140, Easing.CubicIn));
        }
        catch
        {
            // The page may already be disappearing.
        }

        _result.TrySetResult(confirmed);
        try
        {
            // Shell's NavigationImpl can fail to resolve the route after a modal pop on Android
            // (throws "unable to figure out route"). The modal is already dismissed visually,
            // and the result is already delivered above, so this is safe to ignore.
            await Navigation.PopModalAsync(false);
        }
        catch
        {
            // Known MAUI Shell modal issue; ignore and let the app continue.
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }
}
