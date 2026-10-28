using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Controls;

public partial class MiniPlayer : Grid
{
    private readonly PlayerService _playerService = PlayerService.Instance;
    private bool _subscribed;
    private bool _isSeeking;
    private bool _syncingSeek;
    private bool _dockWasVisible;
    private bool _dockAnimating;
    private Page? _hostPage;

    public MiniPlayer()
    {
        InitializeComponent();
        BindingContext = _playerService;
        _dockWasVisible = _playerService.HasCurrentTrack;

        // Start hidden while there is no track so the first frame of the reveal is clean.
        if (!_dockWasVisible)
        {
            Opacity = 0;
            TranslationY = 28;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateSeekUi();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        HookHostPage();
        Subscribe();
        UpdateSeekUi();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        UnhookHostPage();
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _playerService.PropertyChanged += OnPlayerServicePropertyChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _playerService.PropertyChanged -= OnPlayerServicePropertyChanged;
        _subscribed = false;
    }

    /// <summary>
    /// A dock that stays on a page which is navigated away from can miss the Loaded callback
    /// when it comes back. Following the host page's Appearing event guarantees the dock is
    /// listening to the player clock again before the page is visible.
    /// </summary>
    private void HookHostPage()
    {
        var page = FindHostPage();
        if (ReferenceEquals(page, _hostPage))
        {
            return;
        }

        UnhookHostPage();
        _hostPage = page;

        if (page is not null)
        {
            page.Appearing += OnHostPageAppearing;
        }
    }

    private void UnhookHostPage()
    {
        if (_hostPage is null)
        {
            return;
        }

        _hostPage.Appearing -= OnHostPageAppearing;
        _hostPage = null;
    }

    private void OnHostPageAppearing(object? sender, EventArgs e)
    {
        Subscribe();
        UpdateSeekUi();
    }

    private Page? FindHostPage()
    {
        Element? current = this;
        while (current is not null)
        {
            if (current is Page page)
            {
                return page;
            }

            current = current.Parent;
        }

        return null;
    }

    private void OnPlayerServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerService.Position) or nameof(PlayerService.Duration))
        {
            UpdateSeekUi();
        }

        if (e.PropertyName == nameof(PlayerService.CurrentTrack))
        {
            _isSeeking = false;
            UpdateSeekUi();

            var hasTrack = _playerService.HasCurrentTrack;
            var revealDock = hasTrack && !_dockWasVisible;
            _dockWasVisible = hasTrack;

            // Let the IsVisible binding settle for one frame before touching the visuals.
            Dispatcher.DispatchDelayed(
                TimeSpan.FromMilliseconds(16),
                () => _ = revealDock ? AnimateDockInAsync() : AnimateTrackChangeAsync());
        }
    }

    /// <summary>
    /// The first track expands the dock into the page. A short slide and fade makes that
    /// layout change feel intentional without animating height or forcing extra layout work.
    /// </summary>
    private async Task AnimateDockInAsync()
    {
        if (!IsVisible || !_playerService.HasCurrentTrack || _dockAnimating)
        {
            return;
        }

        _dockAnimating = true;

        try
        {
            this.CancelAnimations();
            Opacity = 0;
            TranslationY = 28;

            await Task.WhenAll(
                this.FadeTo(1, 220, Easing.CubicOut),
                this.TranslateTo(0, 0, 260, Easing.CubicOut));
        }
        catch
        {
            // The dock can disappear while a new track is being opened.
        }
        finally
        {
            Opacity = 1;
            TranslationY = 0;
            _dockAnimating = false;
        }
    }

    /// <summary>
    /// A short, compositor-only metadata fade keeps the dock from snapping when the track
    /// changes. It never triggers a layout pass, so it stays cheap during playback.
    /// </summary>
    private async Task AnimateTrackChangeAsync()
    {
        if (!IsVisible || !_playerService.HasCurrentTrack || _dockAnimating)
        {
            return;
        }

        try
        {
            ControlsGrid.CancelAnimations();
            ControlsGrid.Opacity = 0.55;
            ControlsGrid.TranslationY = 3;

            await Task.WhenAll(
                ControlsGrid.FadeTo(1, 150, Easing.CubicOut),
                ControlsGrid.TranslateTo(0, 0, 180, Easing.CubicOut));
        }
        catch
        {
            // The dock can disappear while a new track is being opened.
        }
    }

    /// <summary>
    /// Keeps the seek line and both time labels on the same clock. The slider is a native
    /// control, so its thumb lands exactly where the user taps and its track always matches
    /// the time that is printed next to it.
    /// </summary>
    private void UpdateSeekUi()
    {
        var duration = _playerService.Duration;
        var durationText = FormatTime(duration);
        if (DurationLabel.Text != durationText)
        {
            DurationLabel.Text = durationText;
        }

        _syncingSeek = true;
        try
        {
            var maximum = Math.Max(duration, 1);
            if (Math.Abs(SeekSlider.Maximum - maximum) > 0.01)
            {
                SeekSlider.Maximum = maximum;
            }

            if (!_isSeeking)
            {
                SeekSlider.Value = Math.Clamp(_playerService.Position, 0, SeekSlider.Maximum);
            }
        }
        finally
        {
            _syncingSeek = false;
        }

        if (!_isSeeking)
        {
            var positionText = FormatTime(_playerService.Position);
            if (PositionLabel.Text != positionText)
            {
                PositionLabel.Text = positionText;
            }
        }
    }

    private void OnSeekValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_syncingSeek)
        {
            return;
        }

        PositionLabel.Text = FormatTime(e.NewValue);

        // A tap on the track changes the value without a drag gesture; seek right away so
        // the next clock tick cannot pull the thumb back.
        if (!_isSeeking)
        {
            _playerService.Seek(e.NewValue);
        }
    }

    private void OnSeekDragStarted(object? sender, EventArgs e) => _isSeeking = true;

    private void OnSeekDragCompleted(object? sender, EventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        _playerService.Seek(SeekSlider.Value);
    }

    private async void OnMiniPlayerTapped(object? sender, TappedEventArgs e)
    {
        Haptics.Tap();
        await Motion.PressAsync(this);
        await Shell.Current.GoToAsync(Routes.NowPlaying);
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }
}
