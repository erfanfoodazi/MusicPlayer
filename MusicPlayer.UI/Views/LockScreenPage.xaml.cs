using System.ComponentModel;
using System.Globalization;
using Domain.Models;
using Microsoft.Maui.Dispatching;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;
#if ANDROID
using AndroidX.RecyclerView.Widget;
#endif

namespace MusicPlayer.UI.Views;

/// <summary>
/// Full-screen player that can sit above the Android keyguard. It carries the same synced
/// lyrics as the dedicated Lyrics page, so the lock screen can be used without unlocking.
/// </summary>
public partial class LockScreenPage : ContentPage
{
    private readonly PlayerService _playerService = PlayerService.Instance;
    private static readonly Color LyricsChromeColor = Color.FromArgb("#07070C");
    private Color _accent = Color.FromArgb("#B7A7FF");
    private Color _inactive = Color.FromArgb("#8E82C8");
    private Color? _backdropTop;
    private Color? _backdropBottom;
    private bool _playerSubscribed;
    private bool _scrollSubscribed;
    private bool _scrollScheduled;
    private bool _isSeeking;
    private bool _syncingSeek;
    private bool _syncingVolume;
    private bool _closing;
    private bool _lyricsOpen;
    private bool _queueOpen;
    private bool _queueAnimating;
    private bool _introPlayed;
    private bool _autoResumed;
    private bool _trackAnimating;
    private CancellationTokenSource? _themeCancellation;
    private IDispatcherTimer? _clockTimer;
    private int _pendingScrollIndex = -1;

    private LyricsViewModel ViewModel => (LyricsViewModel)BindingContext;

    public LockScreenPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<LyricsViewModel>();
        QueueOverlay.BindingContext = ServiceHelper.GetRequiredService<NowPlayingViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        SystemChrome.ApplyLockScreen();
#if ANDROID
        LockScreenMode.Enable();
#endif
        _closing = false;
        RootLayout.Opacity = 1;
        RootLayout.TranslationY = 0;
        ResetLyricsOverlay();
        ResetQueueOverlay();

        ViewModel.IsPageVisible = true;
        ViewModel.Refresh();

        SubscribeToPlayer();
        SubscribeToLyrics();

        await ApplyArtworkThemeAsync();
        UpdatePlayerUi();
        TrackTitleMarquee.Start();
        StartClock();

        // The lock player is often opened from a notification or the screen-off gesture.
        // Starting the paused song here means locking the phone never leaves the user with a
        // silent player they have to unlock to resume.
        if (!_autoResumed && _playerService.CurrentTrack is not null &&
            _playerService.State == PlaybackState.Paused)
        {
            _autoResumed = true;
            _playerService.Resume();
        }

        if (!_introPlayed)
        {
            _introPlayed = true;
            _ = RunIntroAsync();
        }

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(320), ScrollToActiveLine);
    }

    /// <summary>
    /// The lock player slides in like a physical object: the clock and artwork settle first,
    /// then the controls and the lyrics card, so the page never appears all at once.
    /// </summary>
    private async Task RunIntroAsync()
    {
        Motion.Prepare(LockHeader, offsetY: -10);
        Motion.Prepare(ClockBlock, offsetY: 14);
        Motion.Prepare(ArtworkCard, offsetY: 24, scale: 0.93);
        Motion.Prepare(TrackBlock, offsetY: 14);
        Motion.Prepare(SeekBlock);
        Motion.Prepare(TransportGrid, offsetY: 20);
        Motion.Prepare(LyricsBar, offsetY: 20);

        await Task.Delay(16);
        await Motion.IntroAsync(
            initialDelay: 0,
            step: 50,
            LockHeader,
            ClockBlock,
            ArtworkCard,
            TrackBlock,
            SeekBlock,
            TransportGrid,
            LyricsBar);
    }

    private void StartClock()
    {
        UpdateClock();

        _clockTimer ??= Dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(5);
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
    }

    private void StopClock()
    {
        if (_clockTimer is null)
        {
            return;
        }

        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
        _clockTimer = null;
    }

    private void OnClockTick(object? sender, EventArgs e) => UpdateClock();

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTimeLabel.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        ClockDateLabel.Text = now
            .ToString("dddd, MMMM d", CultureInfo.CurrentCulture)
            .ToUpper(CultureInfo.CurrentCulture);
    }

    protected override void OnDisappearing()
    {
        ViewModel.IsPageVisible = false;

        TrackTitleMarquee.Stop();
        StopClock();
        UnsubscribeFromPlayer();
        UnsubscribeFromLyrics();

        _themeCancellation?.Cancel();
        _themeCancellation = null;

        _pendingScrollIndex = -1;
        _scrollScheduled = false;

#if ANDROID
        LockScreenMode.Disable();
#endif
        RestoreChrome();

        base.OnDisappearing();
    }

    /// <summary>
    /// The queue sheet and the lyrics overlay are solid dark surfaces, so the system bars
    /// continue whichever surface currently covers the page instead of the cover's tint.
    /// </summary>
    private void ApplyChrome()
    {
        if (_lyricsOpen)
        {
            SystemChrome.ApplyPlayerChrome(LyricsChromeColor, LyricsChromeColor);
            return;
        }

        if (_queueOpen)
        {
            var sheet = (Microsoft.Maui.Controls.Application.Current?.RequestedTheme ?? AppTheme.Dark) == AppTheme.Light
                ? Color.FromArgb("#FFFFFF")
                : Color.FromArgb("#0B0B12");
            SystemChrome.ApplyPlayerChrome(sheet, sheet);
            return;
        }

        if (_backdropTop is { } top && _backdropBottom is { } bottom)
        {
            SystemChrome.ApplyPlayerChrome(top, bottom);
        }
    }

    /// <summary>
    /// Hands the system bars back to whatever is underneath: Now Playing keeps its own cover
    /// colours, everywhere else returns to the app theme.
    /// </summary>
    private static void RestoreChrome()
    {
        var navigation = Shell.Current?.Navigation;
        var nowPlaying = navigation?.ModalStack.OfType<NowPlayingPage>().LastOrDefault()
                         ?? Shell.Current?.CurrentPage as NowPlayingPage;
        if (nowPlaying is not null)
        {
            nowPlaying.RefreshChrome();
            return;
        }

        SystemChrome.ApplyAppTheme();
    }

    protected override bool OnBackButtonPressed()
    {
        if (_queueOpen)
        {
            _ = CloseQueueAsync();
            return true;
        }

        if (_lyricsOpen)
        {
            _ = CloseLyricsAsync();
            return true;
        }

        _ = CloseAsync();
        return true;
    }

    private void SubscribeToPlayer()
    {
        if (_playerSubscribed)
        {
            return;
        }

        _playerService.PropertyChanged += OnPlayerPropertyChanged;
        _playerSubscribed = true;
    }

    private void UnsubscribeFromPlayer()
    {
        if (!_playerSubscribed)
        {
            return;
        }

        _playerService.PropertyChanged -= OnPlayerPropertyChanged;
        _playerSubscribed = false;
    }

    private void SubscribeToLyrics()
    {
        if (_scrollSubscribed)
        {
            return;
        }

        ViewModel.ScrollToLineRequested += OnScrollToLineRequested;
        _scrollSubscribed = true;
    }

    private void UnsubscribeFromLyrics()
    {
        if (!_scrollSubscribed)
        {
            return;
        }

        ViewModel.ScrollToLineRequested -= OnScrollToLineRequested;
        _scrollSubscribed = false;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            ApplyPlayerProperty(e.PropertyName);
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => ApplyPlayerProperty(e.PropertyName));
    }

    private void ApplyPlayerProperty(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(PlayerService.Position):
                UpdatePosition(_playerService.Position);
                break;
            case nameof(PlayerService.Duration):
                UpdatePlaybackUi();
                break;
            case nameof(PlayerService.State):
            case nameof(PlayerService.IsShuffle):
            case nameof(PlayerService.RepeatMode):
                UpdatePlayerUi();
                break;
            case nameof(PlayerService.Volume):
            case nameof(PlayerService.IsMuted):
                UpdateVolumeUi();
                break;
            case nameof(PlayerService.CurrentTrack):
                UpdatePlayerUi();
                _ = ApplyArtworkThemeAsync(animate: true);
                _ = AnimateTrackChangeAsync();
                break;
        }
    }

    /// <summary>
    /// Reads the same cached cover colour as Now Playing and paints this page with it, so the
    /// two players are visually identical even though they are separate pages.
    /// </summary>
    private async Task ApplyArtworkThemeAsync(bool animate = false)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _themeCancellation, cancellation);
        previous?.Cancel();

        Color? accent;
        try
        {
            accent = await ArtworkColors.GetDominantColorAsync(
                _playerService.CurrentTrackArtwork,
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
        var theme = ArtworkColors.CreateTheme(accent, dark);

        _accent = theme.Accent;
        _inactive = theme.AccentMuted;

        BackgroundColor = theme.Base;
        BackdropWash.Background = theme.Backdrop;
        Resources["PlayButtonColor"] = theme.PlayButton;
        Resources["AccentColor"] = theme.Accent;
        Resources["AccentSoftColor"] = theme.AccentSoft;
        Resources["AccentMutedColor"] = theme.AccentMuted;
        Resources["AccentBorderColor"] = theme.AccentBorder;
        Resources["AccentGradient"] = theme.AccentGradient;
        Resources["PanelColor"] = theme.Panel;

        UpdatePlayerUi();

        // The overlays paint their own dark surface, so they own the system bars while open;
        // a track change behind them must not repaint the bars with the new cover's tint.
        _backdropTop = theme.BackdropTop;
        _backdropBottom = theme.BackdropBottom;
        ApplyChrome();

        if (!animate)
        {
            return;
        }

        try
        {
            BackdropWash.CancelAnimations();
            BackdropWash.Opacity = 0.5;
            await BackdropWash.FadeTo(1, 320, Easing.CubicOut);
        }
        catch
        {
            // Rapid track changes can cancel the fade.
        }
    }

    /// <summary>
    /// A new song should feel like a new record being placed: the old cover dims, then the
    /// new one settles back to full size while the title rises into place.
    /// </summary>
    private async Task AnimateTrackChangeAsync()
    {
        if (_trackAnimating || !_introPlayed)
        {
            return;
        }

        _trackAnimating = true;

        try
        {
            ArtworkCard.CancelAnimations();
            TrackBlock.CancelAnimations();

            ArtworkCard.Opacity = 0.3;
            ArtworkCard.Scale = 0.96;
            TrackBlock.Opacity = 0.35;
            TrackBlock.TranslationY = 8;

            await Task.WhenAll(
                ArtworkCard.FadeTo(1, 240, Easing.CubicOut),
                ArtworkCard.ScaleTo(1, 280, Easing.CubicOut),
                TrackBlock.FadeTo(1, 220, Easing.CubicOut),
                TrackBlock.TranslateTo(0, 0, 260, Easing.CubicOut));
        }
        catch
        {
            ArtworkCard.Opacity = 1;
            ArtworkCard.Scale = 1;
            TrackBlock.Opacity = 1;
            TrackBlock.TranslationY = 0;
        }
        finally
        {
            _trackAnimating = false;
        }
    }

    private void UpdatePlayerUi()
    {
        // The slider range has to follow the real duration on every refresh: the page can be
        // opened halfway through a song, when no Duration change will arrive to fix it.
        UpdatePlaybackUi();
        UpdateVolumeUi();

        PlayPauseButton.Text = _playerService.State == PlaybackState.Playing
            ? "\ue034"
            : "\ue037";

        ShuffleButton.TextColor = _playerService.IsShuffle ? _accent : _inactive;
        RepeatButton.Text = _playerService.RepeatMode == RepeatMode.One ? "\ue041" : "\ue040";
        RepeatButton.TextColor = _playerService.RepeatMode == RepeatMode.Off
            ? _inactive
            : _accent;
    }

    private void UpdatePlaybackUi()
    {
        var duration = _playerService.Duration;

        _syncingSeek = true;
        SeekSlider.Maximum = Math.Max(duration, 1);
        if (!_isSeeking)
        {
            SeekSlider.Value = Math.Clamp(_playerService.Position, 0, SeekSlider.Maximum);
        }

        _syncingSeek = false;

        DurationLabel.Text = FormatTime(duration);
        if (!_isSeeking)
        {
            PositionLabel.Text = FormatTime(_playerService.Position);
        }
    }

    private void UpdateVolumeUi()
    {
        _syncingVolume = true;
        VolumeSlider.Value = _playerService.Volume;
        _syncingVolume = false;
        MuteButton.Text = _playerService.MuteGlyph;
    }

    private void OnVolumeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_syncingVolume)
        {
            return;
        }

        _playerService.SetVolume(e.NewValue);
    }

    private void OnMuteClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.ToggleMute();
    }

    private void UpdatePosition(double position)
    {
        if (_isSeeking)
        {
            return;
        }

        _syncingSeek = true;
        SeekSlider.Value = Math.Clamp(position, 0, SeekSlider.Maximum);
        _syncingSeek = false;
        PositionLabel.Text = FormatTime(position);
    }

    private void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_syncingSeek)
        {
            return;
        }

        PositionLabel.Text = FormatTime(e.NewValue);

        // A tap changes the slider value without necessarily raising DragStarted. Seek right
        // away in that case so the next player tick cannot pull the thumb back.
        if (!_isSeeking)
        {
            _playerService.Seek(e.NewValue);
        }
    }

    private void OnSeekDragStarted(object? sender, EventArgs e)
    {
        _isSeeking = true;
    }

    private void OnSeekDragCompleted(object? sender, EventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        _playerService.Seek(SeekSlider.Value);
        UpdatePlayerUi();
    }

    private void OnPreviousClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.PlayPrevious();
    }

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.TogglePlayPause();
        _ = Motion.PulseAsync(PlayPauseButton, 1.05);
    }

    private void OnNextClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.PlayNext();
    }

    private void OnShuffleClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.ToggleShuffle();
        _ = Motion.PulseAsync(ShuffleButton, 1.12);
    }

    private void OnRepeatClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        _playerService.CycleRepeatMode();
        _ = Motion.PulseAsync(RepeatButton, 1.12);
    }

    /// <summary>
    /// Swipe up anywhere on the clock, cover or title to leave the lock player, the same way
    /// the system keyguard is dismissed. The page follows the finger and springs back if the
    /// gesture is too short.
    /// </summary>
    private void OnDragPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                RootLayout.CancelAnimations();
                break;

            case GestureStatus.Running:
                if (e.TotalY < 0)
                {
                    RootLayout.TranslationY = e.TotalY * 0.55;
                }

                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (RootLayout.TranslationY < -36)
                {
                    _ = CloseAsync();
                }
                else
                {
                    _ = RootLayout.TranslateTo(0, 0, 180, Easing.CubicOut);
                }

                break;
        }
    }

    private void OnLyricLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: LyricsLineViewModel line })
        {
            ViewModel.SeekToLineCommand.Execute(line);
        }
    }

    /// <summary>
    /// The lock player keeps its transport visible and opens the words only when asked, the
    /// same way the in-app player does. The overlay lives inside this page because a Shell
    /// route would be pushed underneath the lock-screen modal.
    /// </summary>
    private async void OnLyricsTapped(object? sender, TappedEventArgs e)
    {
        if (_lyricsOpen || _queueOpen)
        {
            return;
        }

        _lyricsOpen = true;
        Haptics.Tap();
        ApplyChrome();

        LyricsOverlay.IsVisible = true;
        LyricsOverlay.Opacity = 0;
        LyricsOverlay.TranslationY = 36;

        ScrollToActiveLine();

        try
        {
            await Task.WhenAll(
                LyricsOverlay.FadeTo(1, 190, Easing.CubicOut),
                LyricsOverlay.TranslateTo(0, 0, 240, Easing.CubicOut));
        }
        catch
        {
            LyricsOverlay.Opacity = 1;
            LyricsOverlay.TranslationY = 0;
        }

        ScrollToActiveLine();
    }

    private async void OnLyricsCloseClicked(object? sender, EventArgs e) => await CloseLyricsAsync();

    private async Task CloseLyricsAsync()
    {
        if (!_lyricsOpen)
        {
            return;
        }

        _lyricsOpen = false;
        Haptics.Tap();

        try
        {
            await Task.WhenAll(
                LyricsOverlay.FadeTo(0, 140, Easing.CubicIn),
                LyricsOverlay.TranslateTo(0, 28, 170, Easing.CubicIn));
        }
        catch
        {
            // A page being dismissed cancels the animation.
        }

        ResetLyricsOverlay();
        ApplyChrome();
    }

    private void ResetLyricsOverlay()
    {
        _lyricsOpen = false;
        LyricsOverlay.IsVisible = false;
        LyricsOverlay.Opacity = 1;
        LyricsOverlay.TranslationY = 0;
    }

    private void OnQueueTapped(object? sender, TappedEventArgs e) => _ = OpenQueueAsync();

    /// <summary>
    /// The queue is the same session list Now Playing shows, bound to the same singleton view
    /// model, so reordering the sheet here and in the app can never disagree.
    /// </summary>
    private async Task OpenQueueAsync()
    {
        try
        {
            await OpenQueueCoreAsync();
        }
        catch
        {
            // A failed open must leave the overlay closed and the button usable again.
            ResetQueueOverlay();
            _queueAnimating = false;
        }
    }

    private async Task OpenQueueCoreAsync()
    {
        if (_queueAnimating || _queueOpen || _lyricsOpen)
        {
            return;
        }

        _queueAnimating = true;
        _queueOpen = true;
        Haptics.Tap();

        ((NowPlayingViewModel)QueueOverlay.BindingContext).Refresh();

        QueueOverlay.IsVisible = true;
        QueueScrim.Opacity = 0;
        QueueSheet.TranslationY = Math.Max(QueueSheet.Height, 620);

        ApplyChrome();

        try
        {
            await Task.WhenAll(
                QueueScrim.FadeTo(1, 160, Easing.CubicOut),
                QueueSheet.TranslateTo(0, 0, 280, Easing.CubicOut));
        }
        catch
        {
            QueueScrim.Opacity = 1;
            QueueSheet.TranslationY = 0;
        }

        _queueAnimating = false;
    }

    private async Task CloseQueueAsync()
    {
        if (_queueAnimating || !_queueOpen)
        {
            return;
        }

        _queueAnimating = true;
        _queueOpen = false;
        Haptics.Tap();

        try
        {
            await Task.WhenAll(
                QueueScrim.FadeTo(0, 130, Easing.CubicIn),
                QueueSheet.TranslateTo(0, Math.Max(QueueSheet.Height, 620), 220, Easing.CubicIn));
        }
        catch
        {
            // The lock-screen modal can be dismissed mid-animation.
        }

        ResetQueueOverlay();
        ApplyChrome();
        _queueAnimating = false;
    }

    private void ResetQueueOverlay()
    {
        _queueOpen = false;
        QueueOverlay.IsVisible = false;
        QueueScrim.Opacity = 0;
        QueueSheet.TranslationY = 0;
    }

    private async void OnQueueScrimTapped(object? sender, TappedEventArgs e) => await CloseQueueAsync();

    private void OnQueueCloseClicked(object? sender, EventArgs e) => _ = CloseQueueAsync();

    private void OnClearQueueClicked(object? sender, EventArgs e)
    {
        Haptics.Tap();
        ((NowPlayingViewModel)QueueOverlay.BindingContext).ClearQueueCommand.Execute(null);
    }

    private void OnQueueRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: QueueRow row })
        {
            Haptics.Tap();
            ((NowPlayingViewModel)QueueOverlay.BindingContext).PlayQueueItemCommand.Execute(row.Track);
        }
    }

    private void OnRemoveQueueItemClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Music track })
        {
            Haptics.Tap();
            ((NowPlayingViewModel)QueueOverlay.BindingContext).RemoveFromQueueCommand.Execute(track);
        }
    }

    private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    private async Task CloseAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
#if ANDROID
        LockScreenMode.Disable();
#endif

        try
        {
            // Fade the whole sheet down before the shell pops it, so the lock player never
            // disappears between one frame and the next.
            if (RootLayout.Opacity > 0.4)
            {
                await Task.WhenAll(
                    RootLayout.FadeTo(0, 150, Easing.CubicIn),
                    RootLayout.TranslateTo(0, 46, 190, Easing.CubicIn));
            }
        }
        catch
        {
            // A page already being dismissed can cancel the animation.
        }

        try
        {
            var navigation = Shell.Current?.Navigation;
            if (navigation is not null && navigation.ModalStack.Contains(this))
            {
                await navigation.PopModalAsync(false);
            }
        }
        catch
        {
            // The page may already have been dismissed by the system.
        }

        RestoreChrome();
    }

    private void OnScrollToLineRequested(object? sender, int index) => ScrollToLine(index);

    private void ScrollToActiveLine() => ScrollToLine(ViewModel.ActiveLineIndex);

    private void ScrollToLine(int index)
    {
        if (index < 0 || index >= ViewModel.Lines.Count)
        {
            return;
        }

        _pendingScrollIndex = index;
        if (_scrollScheduled)
        {
            return;
        }

        _scrollScheduled = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(70), FlushScrollToLine);
    }

    private void FlushScrollToLine()
    {
        _scrollScheduled = false;

        var index = _pendingScrollIndex;
        _pendingScrollIndex = -1;

        if (index < 0 || index >= ViewModel.Lines.Count)
        {
            return;
        }

        try
        {
#if ANDROID
            if (LyricsList.Handler?.PlatformView is RecyclerView recycler &&
                recycler.Context is { } context &&
                recycler.GetLayoutManager() is { } layoutManager)
            {
                var adapterIndex = index + (LyricsList.Header is not null ? 1 : 0);
                layoutManager.StartSmoothScroll(
                    new Platforms.Android.LyricsSmoothScroller(context, adapterIndex));
                return;
            }
#endif
            LyricsList.ScrollTo(index, position: ScrollToPosition.Center, animate: true);
        }
        catch
        {
            // Virtualized rows can be re-templated while lyrics are loading.
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }
}
