using System.ComponentModel;
using Domain.Models;
using Microsoft.Maui.Graphics;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class NowPlayingPage : ContentPage
{
    private bool _queueOpen;
    private bool _queueAnimating;
    private bool _subscribed;
    private bool _skipNextIntro;
    private CancellationTokenSource? _backdropCancellation;

    private NowPlayingViewModel ViewModel => (NowPlayingViewModel)BindingContext;

    public NowPlayingPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<NowPlayingViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        SystemChrome.ApplyAppTheme();
        ViewModel.Refresh();

        if (!_subscribed)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribed = true;
        }

        await ApplyBackdropAsync();

        if (_skipNextIntro)
        {
            // Returning from lyrics or the lock-screen player should not replay the intro.
            _skipNextIntro = false;
            SetIntroComplete();
        }
        else
        {
            await AnimateInAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _backdropCancellation?.Cancel();

        if (_subscribed)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed = false;
        }

        SystemChrome.ApplyAppTheme();
    }

    /// <summary>
    /// Compositor-only page intro. The page background is already on screen, so the header,
    /// artwork, details and transport settle in without a blank frame or a layout pass.
    /// </summary>
    private async Task AnimateInAsync()
    {
        try
        {
            PageHeader.CancelAnimations();
            ContentScroll.CancelAnimations();
            TransportRow.CancelAnimations();
            ArtworkCard.CancelAnimations();
            AmbientBackdrop.CancelAnimations();

            PageHeader.Opacity = 0;
            PageHeader.TranslationY = -10;
            ContentScroll.Opacity = 0;
            ContentScroll.TranslationY = 18;
            TransportRow.Opacity = 0;
            TransportRow.TranslationY = 24;
            ArtworkCard.Opacity = 0;
            ArtworkCard.Scale = 0.97;
            ArtworkCard.TranslationY = 14;
            AmbientBackdrop.Opacity = 0;

            await Task.Delay(16);

            await Task.WhenAll(
                PageHeader.FadeTo(1, 180, Easing.CubicOut),
                PageHeader.TranslateTo(0, 0, 220, Easing.CubicOut),
                ContentScroll.FadeTo(1, 260, Easing.CubicOut),
                ContentScroll.TranslateTo(0, 0, 300, Easing.CubicOut),
                TransportRow.FadeTo(1, 280, Easing.CubicOut),
                TransportRow.TranslateTo(0, 0, 320, Easing.CubicOut),
                ArtworkCard.FadeTo(1, 240, Easing.CubicOut),
                ArtworkCard.ScaleTo(1, 320, Easing.CubicOut),
                ArtworkCard.TranslateTo(0, 0, 320, Easing.CubicOut),
                AmbientBackdrop.FadeTo(1, 420, Easing.CubicOut));
        }
        catch
        {
            // Animations can be cancelled when the page is dismissed quickly.
            SetIntroComplete();
        }
    }

    private void SetIntroComplete()
    {
        PageHeader.CancelAnimations();
        ContentScroll.CancelAnimations();
        TransportRow.CancelAnimations();
        ArtworkCard.CancelAnimations();
        AmbientBackdrop.CancelAnimations();

        PageHeader.Opacity = 1;
        PageHeader.TranslationY = 0;
        ContentScroll.Opacity = 1;
        ContentScroll.TranslationY = 0;
        TransportRow.Opacity = 1;
        TransportRow.TranslationY = 0;
        ArtworkCard.Opacity = 1;
        ArtworkCard.Scale = 1;
        ArtworkCard.TranslationY = 0;
        AmbientBackdrop.Opacity = 1;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentTrack))
        {
            _ = AnimateTrackChangeAsync();
        }
        else if (e.PropertyName == nameof(NowPlayingViewModel.State))
        {
            // A small pulse confirms transport changes without moving the button.
            _ = Motion.PulseAsync(PlayPauseButton, 1.05);
        }
    }

    private async Task AnimateTrackChangeAsync()
    {
        try
        {
            CoverImage.CancelAnimations();

            // Give the artwork binding one frame, then settle into the new cover. Using a
            // partial fade instead of a full blank removes the flash between tracks.
            await Task.Delay(16);

            CoverImage.Opacity = 0.55;
            CoverImage.Scale = 1.012;

            await Task.WhenAll(
                CoverImage.FadeTo(1, 200, Easing.CubicOut),
                CoverImage.ScaleTo(1, 240, Easing.CubicOut));
        }
        catch
        {
            // Ignore rapid track changes.
        }

        await ApplyBackdropAsync(animate: true);
    }

    /// <summary>
    /// Paints the page with the dominant colour of the current cover. Extraction is cached per
    /// file, so revisiting a song or an album is instant.
    /// </summary>
    private async Task ApplyBackdropAsync(bool animate = false)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _backdropCancellation, cancellation);
        previous?.Cancel();

        Color? accent;
        try
        {
            accent = await ArtworkColors.GetDominantColorAsync(
                PlayerService.Instance.CurrentTrackArtwork,
                cancellation.Token);
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

        if (!animate)
        {
            return;
        }

        try
        {
            AmbientBackdrop.CancelAnimations();
            AmbientBackdrop.Opacity = 0.45;
            await AmbientBackdrop.FadeTo(1, 320, Easing.CubicOut);
        }
        catch
        {
            // Rapid track changes can cancel the fade.
        }
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        if (_queueOpen)
        {
            await CloseQueueAsync();
            return;
        }

        await Shell.Current.GoToAsync("..");
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        if (sender is Slider slider)
        {
            ViewModel.SeekCommand.Execute(slider.Value);
        }
    }

    private async void OnLyricsClicked(object? sender, EventArgs e)
    {
        _skipNextIntro = true;
        await Shell.Current.GoToAsync(Routes.Lyrics);
    }

    // ------------------------------------------------------------ audio tools

    private async void OnSpeedClicked(object? sender, EventArgs e)
    {
        var player = PlayerService.Instance;
        if (!player.SupportsPlaybackRate)
        {
            await AppDialogs.ShowAlertAsync(this, "Not available", "Playback speed is only available on Android and Windows in this build.");
            return;
        }

        var options = new (double Value, string Label)[]
        {
            (0.5, "0.5× · Half speed"),
            (0.75, "0.75× · Slow"),
            (0.9, "0.9×"),
            (1.0, "1× · Normal"),
            (1.1, "1.1×"),
            (1.25, "1.25× · Faster"),
            (1.5, "1.5×"),
            (1.75, "1.75×"),
            (2.0, "2× · Double speed"),
        };

        var actions = options
            .Select(option => new AppSheetAction
            {
                Label = option.Label,
                Glyph = "\ue9e4",
                Description = "Pro · keeps the pitch natural",
                IsSelected = Math.Abs(player.PlaybackSpeed - option.Value) < 0.01,
            })
            .ToList();

        var index = await AppDialogs.ShowActionsAsync(this, "Playback speed", actions, "PRO · audio plays faster without chipmunk pitch");
        if (index < 0 || index >= options.Length)
        {
            return;
        }

        player.SetPlaybackSpeed(options[index].Value);
        Haptics.Tap();
    }

    private async void OnPitchClicked(object? sender, EventArgs e)
    {
        var player = PlayerService.Instance;
        if (!player.SupportsPitch)
        {
            await AppDialogs.ShowAlertAsync(this, "Not available", "Independent pitch is only available on Android in this build.");
            return;
        }

        var actions = new List<AppSheetAction>();
        for (var semitone = -6; semitone <= 6; semitone++)
        {
            var label = semitone switch
            {
                0 => "0 · Original pitch",
                > 0 => $"+{semitone} semitone{(semitone == 1 ? "" : "s")}",
                _ => $"{semitone} semitone{(semitone == -1 ? "" : "s")}"
            };

            actions.Add(new AppSheetAction
            {
                Label = label,
                Glyph = "\ue405",
                Description = "Pro · independent pitch shift",
                IsSelected = player.PitchSemitones == semitone,
            });
        }

        var choice = await AppDialogs.ShowActionsAsync(this, "Pitch", actions, "PRO · shift the key, independent of speed");
        if (choice < 0)
        {
            return;
        }

        player.SetPitchSemitones(choice - 6);
        Haptics.Tap();
    }

    private async void OnSleepClicked(object? sender, EventArgs e)
    {
        var player = PlayerService.Instance;
        var durations = new (string Label, TimeSpan? Duration, bool EndOfTrack)[]
        {
            ("Off", null, false),
            ("5 minutes", TimeSpan.FromMinutes(5), false),
            ("10 minutes", TimeSpan.FromMinutes(10), false),
            ("15 minutes", TimeSpan.FromMinutes(15), false),
            ("30 minutes", TimeSpan.FromMinutes(30), false),
            ("45 minutes", TimeSpan.FromMinutes(45), false),
            ("60 minutes", TimeSpan.FromMinutes(60), false),
            ("End of track", null, true),
        };

        var actions = durations
            .Select((option, index) => new AppSheetAction
            {
                Label = option.Label,
                Glyph = index == 0 ? "\ue8f4" : "\ue8b2",
                Description = option.EndOfTrack
                    ? "Pause after the current song finishes"
                    : index == 0 ? "Cancel the sleep timer" : "Auralis will pause automatically",
                IsSelected = index == 0
                    ? player.SleepTimerDisplay == "Off"
                    : option.EndOfTrack && player.SleepTimerDisplay == "End of track",
            })
            .ToList();

        var choice = await AppDialogs.ShowActionsAsync(this, "Sleep timer", actions, "Fall asleep to the music");
        if (choice < 0 || choice >= durations.Length)
        {
            return;
        }

        var selected = durations[choice];
        player.SetSleepTimer(selected.Duration, selected.EndOfTrack);
        Haptics.Tap();
    }

    private async void OnEqualizerClicked(object? sender, EventArgs e)
    {
        var player = PlayerService.Instance;
        if (!player.SupportsEqualizer)
        {
            await AppDialogs.ShowAlertAsync(this, "Not available", "The equalizer is available on Android in this build.");
            return;
        }

        var presets = player.EqualizerPresets;
        if (presets.Count == 0)
        {
            await AppDialogs.ShowAlertAsync(
                this,
                "Equalizer warming up",
                "Start a song, then open the equalizer again. Android attaches audio effects to the active playback session.",
                "OK");
            return;
        }

        var actions = new List<AppSheetAction>
        {
            new()
            {
                Label = "Off",
                Glyph = "\ue8f4",
                Description = "Bypass the equalizer",
                IsSelected = !player.IsEqualizerEnabled,
            }
        };

        actions.AddRange(presets.Select(preset => new AppSheetAction
        {
            Label = preset,
            Glyph = "\uea4f",
            Description = "Pro · device sound profile",
            IsSelected = player.IsEqualizerEnabled &&
                         string.Equals(preset, player.EqualizerPreset, StringComparison.OrdinalIgnoreCase),
        }));

        var choice = await AppDialogs.ShowActionsAsync(
            this,
            "Equalizer",
            actions,
            "PRO · powered by your device audio engine");

        if (choice < 0)
        {
            return;
        }

        if (choice == 0)
        {
            player.SetEqualizerEnabled(false);
        }
        else
        {
            player.SetEqualizerPreset(presets[choice - 1]);
        }

        Haptics.Tap();
    }

    private async void OnQueueClicked(object? sender, EventArgs e)
    {
        if (_queueOpen)
        {
            await CloseQueueAsync();
        }
        else
        {
            await OpenQueueAsync();
        }
    }

    private async Task OpenQueueAsync()
    {
        if (_queueAnimating)
        {
            return;
        }

        _queueAnimating = true;
        _queueOpen = true;
        QueueOverlay.IsVisible = true;
        QueueScrim.Opacity = 0;
        QueueSheet.TranslationY = Math.Max(QueueSheet.Height, 620);

        // The sheet always opens at the top of the session (previously played first), while
        // the current song stays visible in the pinned card above the list.
        ScrollQueueToTop();

        await Task.WhenAll(
            QueueScrim.FadeTo(1, 160, Easing.CubicOut),
            QueueSheet.TranslateTo(0, 0, 280, Easing.CubicOut));

        _queueAnimating = false;
    }

    /// <summary>Opens the queue at the first row of the first group.</summary>
    private void ScrollQueueToTop()
    {
        if (ViewModel.QueueSections.Count == 0)
        {
            return;
        }

        try
        {
            // Grouped lists want both indexes; the item index on its own throws on some
            // platforms, so fall back to the plain overload.
            QueueList.ScrollTo(0, 0, ScrollToPosition.Start, animate: false);
        }
        catch
        {
            try
            {
                QueueList.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
            }
            catch
            {
                // Scrolling is cosmetic; never let it block the sheet.
            }
        }
    }

    private async Task CloseQueueAsync()
    {
        if (_queueAnimating || !_queueOpen)
        {
            return;
        }

        _queueAnimating = true;
        _queueOpen = false;

        await Task.WhenAll(
            QueueScrim.FadeTo(0, 130, Easing.CubicIn),
            QueueSheet.TranslateTo(0, Math.Max(QueueSheet.Height, 620), 220, Easing.CubicIn));

        QueueOverlay.IsVisible = false;
        QueueSheet.TranslationY = 0;
        _queueAnimating = false;
    }

    private async void OnQueueScrimTapped(object? sender, TappedEventArgs e) => await CloseQueueAsync();

    private void OnClearQueueClicked(object? sender, EventArgs e)
    {
        ViewModel.ClearQueueCommand.Execute(null);
        Haptics.Tap();
    }

    private void OnQueueRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: QueueRow row })
        {
            Haptics.Tap();
            ViewModel.PlayQueueItemCommand.Execute(row.Track);
        }
    }

    private void OnRemoveQueueItemClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Music track })
        {
            ViewModel.RemoveFromQueueCommand.Execute(track);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_queueOpen)
        {
            _ = CloseQueueAsync();
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
