using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Views;

/// <summary>
/// Styled text prompt used for creating and renaming playlists. Resolves with the entered
/// text, or null when the user cancels or dismisses it.
/// </summary>
public partial class InputDialogPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly bool _rejectEmpty;
    private bool _closing;

    public Task<string?> Result => _result.Task;

    public InputDialogPage(
        string title,
        string confirmText = "Create",
        string? hint = null,
        string? initialValue = null,
        bool rejectEmpty = true)
    {
        InitializeComponent();

        NavigationPage.SetHasNavigationBar(this, false);
        TitleLabel.Text = title;
        ConfirmButton.Text = confirmText;
        _rejectEmpty = rejectEmpty;

        HintLabel.Text = hint ?? string.Empty;
        HintLabel.IsVisible = !string.IsNullOrWhiteSpace(hint);

        if (!string.IsNullOrWhiteSpace(initialValue))
        {
            InputEntry.Text = initialValue;
        }

        BindingContext = this;
    }

    /// <summary>Bound to the entry's Return key so Enter confirms like a native dialog.</summary>
    public System.Windows.Input.ICommand ConfirmCommand => new Command(() => _ = ConfirmAsync());

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        Card.Opacity = 0;
        Card.Scale = 0.94;
        await Task.WhenAll(
            Card.FadeTo(1, 160, Easing.CubicOut),
            Card.ScaleTo(1, 200, Easing.CubicOut));

        // Focus after the animation so the keyboard does not fight the scale transition.
        await Task.Delay(180);
        InputEntry.Focus();
        if (!string.IsNullOrWhiteSpace(InputEntry.Text))
        {
            InputEntry.CursorPosition = InputEntry.Text.Length;
        }
    }

    private async void OnConfirmClicked(object? sender, EventArgs e) => await ConfirmAsync();

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task ConfirmAsync()
    {
        var text = InputEntry.Text?.Trim() ?? string.Empty;

        if (_rejectEmpty && string.IsNullOrWhiteSpace(text))
        {
            ErrorLabel.Text = "Please type a name.";
            ErrorLabel.IsVisible = true;
            return;
        }

        Haptics.Tap();
        await CloseAsync(string.IsNullOrWhiteSpace(text) ? null : text);
    }

    private async Task CloseAsync(string? value)
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
            // A cancelled animation still means the page is closing.
        }

        _result.TrySetResult(value);
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
        _ = CloseAsync(null);
        return true;
    }
}
