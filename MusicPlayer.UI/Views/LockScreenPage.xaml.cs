using System.ComponentModel;
using Domain.Models;
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
    private static readonly Color AccentColor = Color.FromArgb("#A78BFF");
    private static readonly Color InactiveControlColor = Color.FromArgb("#77778C");

    private readonly PlayerService _playerService = PlayerService.Instance;
    private bool _playerSubscribed;
    private bool _scrollSubscribed;
    private bool _scrollScheduled;
    private bool _isSeeking;
    private bool _syncingSeek;
    private bool _closing;
    private bool _introPlayed;
    private int _pendingScrollIndex = -1;

    private LyricsViewModel ViewModel => (LyricsViewModel)BindingContext;

    public LockScreenPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<LyricsViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        SystemChrome.ApplyLockScreen();
#if ANDROID
        LockScreenMode.Enable();
#endif
        _closing = false;

        ViewModel.IsPageVisible = true;
        ViewModel.Refresh();

        SubscribeToPlayer();
        SubscribeToLyrics();

        UpdatePlayerUi();

        if (!_introPlayed)
        {
            _introPlayed = true;
            _ = RunIntroAsync();
        }

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(320), ScrollToActiveLine);
    }

    /// <summary>
    /// The lock player slides in like a physical object: artwork settles first, then the
    /// controls and the lyrics card, so the page never appears all at once.
    /// </summary>
    private async Task RunIntroAsync()
    {
        Motion.Prepare(LockHeader, offsetY: -8);
        Motion.Prepare(ArtworkCard, offsetY: 22, scale: 0.94);
        Motion.Prepare(TrackBlock);
        Motion.Prepare(SeekBlock);
        Motion.Prepare(TransportGrid, offsetY: 18);
        Motion.Prepare(LyricsCard, offsetY: 24);

        await Task.Delay(16);
        await Motion.IntroAsync(
            initialDelay: 0,
            step: 55,
            LockHeader,
            ArtworkCard,
            TrackBlock,
            SeekBlock,
            TransportGrid,
            LyricsCard);
    }

    protected override void OnDisappearing()
    {
        ViewModel.IsPageVisible = false;

        UnsubscribeFromPlayer();
        UnsubscribeFromLyrics();

        _pendingScrollIndex = -1;
        _scrollScheduled = false;

#if ANDROID
        LockScreenMode.Disable();
#endif
        SystemChrome.ApplyAppTheme();

        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
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
            case nameof(PlayerService.CurrentTrack):
            case nameof(PlayerService.IsShuffle):
            case nameof(PlayerService.RepeatMode):
                UpdatePlayerUi();
                break;
        }
    }

    private void UpdatePlayerUi()
    {
        // The slider range has to follow the real duration on every refresh: the page can be
        // opened halfway through a song, when no Duration change will arrive to fix it.
        UpdatePlaybackUi();

        PlayPauseButton.Text = _playerService.State == PlaybackState.Playing
            ? "\ue034"
            : "\ue037";

        ShuffleButton.TextColor = _playerService.IsShuffle ? AccentColor : InactiveControlColor;
        RepeatButton.Text = _playerService.RepeatMode == RepeatMode.One ? "\ue041" : "\ue040";
        RepeatButton.TextColor = _playerService.RepeatMode == RepeatMode.Off
            ? InactiveControlColor
            : AccentColor;
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

    private void OnPreviousClicked(object? sender, EventArgs e) => _playerService.PlayPrevious();

    private void OnPlayPauseClicked(object? sender, EventArgs e) => _playerService.TogglePlayPause();

    private void OnNextClicked(object? sender, EventArgs e) => _playerService.PlayNext();

    private void OnShuffleClicked(object? sender, EventArgs e) => _playerService.ToggleShuffle();

    private void OnRepeatClicked(object? sender, EventArgs e) => _playerService.CycleRepeatMode();

    private void OnLyricLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: LyricsLineViewModel line })
        {
            ViewModel.SeekToLineCommand.Execute(line);
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
        SystemChrome.ApplyAppTheme();

        try
        {
            if (Shell.Current.Navigation.NavigationStack.Count > 1)
            {
                await Shell.Current.GoToAsync("..");
            }
        }
        catch
        {
            // The page may already have been popped by the system.
        }
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
