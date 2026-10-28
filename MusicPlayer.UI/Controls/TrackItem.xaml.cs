using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Controls;

public partial class TrackItem : Grid
{
    public static readonly BindableProperty TrackProperty =
        BindableProperty.Create(nameof(Track), typeof(Music), typeof(TrackItem), propertyChanged: OnTrackChanged);

    /// <summary>
    /// Album pages show the track's position instead of repeating the same sleeve on every
    /// row. Defaults to the artwork treatment used by search, playlists and queue lists.
    /// </summary>
    public static readonly BindableProperty ShowTrackNumberProperty =
        BindableProperty.Create(
            nameof(ShowTrackNumber),
            typeof(bool),
            typeof(TrackItem),
            false,
            propertyChanged: OnShowTrackNumberChanged);

    /// <summary>
    /// Playlist rows can be long-pressed and dragged. The gestures live on the row itself
    /// because its tap recognizer consumes touches before a wrapping container sees them.
    /// </summary>
    public static readonly BindableProperty IsReorderableProperty =
        BindableProperty.Create(
            nameof(IsReorderable),
            typeof(bool),
            typeof(TrackItem),
            false,
            propertyChanged: OnIsReorderableChanged);

    public event EventHandler<DragStartingEventArgs>? RowDragStarting;
    public event EventHandler<DropCompletedEventArgs>? RowDragCompleted;
    public event EventHandler<DropEventArgs>? RowDrop;

    private readonly DragGestureRecognizer _drag;
    private readonly DropGestureRecognizer _drop;

    private readonly PlayerService _playerService = PlayerService.Instance;
    private bool _subscribed;

    public Music? Track
    {
        get => (Music?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public bool ShowTrackNumber
    {
        get => (bool)GetValue(ShowTrackNumberProperty);
        set => SetValue(ShowTrackNumberProperty, value);
    }

    public bool IsReorderable
    {
        get => (bool)GetValue(IsReorderableProperty);
        set => SetValue(IsReorderableProperty, value);
    }

    public TrackItem()
    {
        InitializeComponent();

        _drag = new DragGestureRecognizer { CanDrag = false };
        _drag.DragStarting += (_, e) => RowDragStarting?.Invoke(this, e);
        _drag.DropCompleted += (_, e) => RowDragCompleted?.Invoke(this, e);

        _drop = new DropGestureRecognizer { AllowDrop = false };
        _drop.Drop += (_, e) => RowDrop?.Invoke(this, e);

        GestureRecognizers.Add(_drag);
        GestureRecognizers.Add(_drop);

        UpdateTrackNumberVisibility();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnIsReorderableChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is TrackItem item)
        {
            var enabled = (bool)newValue;
            item._drag.CanDrag = enabled;
            item._drop.AllowDrop = enabled;
        }
    }

    private static void OnTrackChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is TrackItem item)
        {
            // Never assign BindingContext here. CollectionView recycles these rows and an
            // explicit local context sticks to the first track, so recycled rows keep
            // showing the old song. The inherited context (the item itself) drives the
            // labels; this callback only refreshes the non-binding visuals.
            item.UpdateTrackNumberVisibility();
            item.UpdateCurrentState();
        }
    }

    private static void OnShowTrackNumberChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is TrackItem item)
        {
            item.UpdateTrackNumberVisibility();
        }
    }

    private void UpdateTrackNumberVisibility()
    {
        var showNumber = ShowTrackNumber;
        CoverArt.IsVisible = !showNumber;
        TrackNumberLabel.IsVisible = showNumber;
        TrackNumberLabel.Text = showNumber
            ? Track?.TrackNumber?.ToString("00") ?? string.Empty
            : string.Empty;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (!_subscribed)
        {
            _playerService.PropertyChanged += OnPlayerPropertyChanged;
            _subscribed = true;
        }

        UpdateCurrentState();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_subscribed)
        {
            _playerService.PropertyChanged -= OnPlayerPropertyChanged;
            _subscribed = false;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerService.CurrentTrack) or nameof(PlayerService.State))
        {
            UpdateCurrentState();
        }
    }

    /// <summary>
    /// Tints the active row instead of adding a per-row subscription to every list. Only the
    /// handful of visible rows are subscribed at any time because rows unload when recycled.
    /// </summary>
    private void UpdateCurrentState()
    {
        var current = _playerService.CurrentTrack;
        var isCurrent = current is not null && Track is not null &&
                        (ReferenceEquals(current, Track) || current.Id == Track.Id);

        RowHighlight.IsVisible = isCurrent;
        PlayingIcon.IsVisible = isCurrent;

        if (isCurrent)
        {
            var accent = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"];
            RowHighlight.BackgroundColor = accent.WithAlpha(0.12f);
            TitleLabel.TextColor = accent;
        }
        else
        {
            var themeKey = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light
                ? "TextOnLight"
                : "TextOnDark";
            TitleLabel.TextColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[themeKey];
        }
    }

    private async void OnTrackTapped(object? sender, TappedEventArgs e)
    {
        if (Track is null) return;

        Haptics.Tap();

        // The cover opens Now Playing; the rest of the row is a quick "play this"
        // action that leaves the current page in place. Using the tap position avoids
        // nested gesture recognizers firing twice on Android.
        // Tactile press feedback; never awaited so playback starts on the same frame.
        _ = Motion.PressAsync(this);

        var position = e.GetPosition(this);
        var opensPlayer = position is { } point &&
                          point.X >= CoverArt.X - 4 &&
                          point.X <= CoverArt.X + CoverArt.Width + 4 &&
                          point.Y >= CoverArt.Y - 4 &&
                          point.Y <= CoverArt.Y + CoverArt.Height + 4;

        if (opensPlayer)
        {
            await OpenTrackAsync(Track);
        }
        else
        {
            PlayTrack(Track);
        }
    }

    private void PlayTrack(Music track)
    {
        // The hosting page knows which list this item belongs to, so let its
        // PlayTrack command replace the queue with the correct context.
        if (Shell.Current?.CurrentPage?.BindingContext is IPlayTrackContext context &&
            context.PlayTrackCommand.CanExecute(track))
        {
            context.PlayTrackCommand.Execute(track);
            return;
        }

        PlayerService.Instance.Play(track);
    }

    private async Task OpenTrackAsync(Music track)
    {
        if (Shell.Current?.CurrentPage?.BindingContext is IPlayTrackContext context &&
            context.OpenTrackCommand.CanExecute(track))
        {
            context.OpenTrackCommand.Execute(track);
            return;
        }

        PlayerService.Instance.Play(track);
        var shell = Shell.Current;
        if (shell is not null)
        {
            await shell.GoToAsync(Routes.NowPlaying);
        }
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        if (Track is null) return;

        var page = Shell.Current?.CurrentPage;
        if (page is null) return;

        // The row can be recycled while the sheet is open, so keep a snapshot.
        var track = Track;

        var actions = new List<AppSheetAction>
        {
            new() { Label = "Play now", Glyph = "\ue037", Description = "Start playing this track" },
            new() { Label = "Add to queue", Glyph = "\ue03d", Description = "Play it after the current song" },
            new() { Label = "Add to playlist", Glyph = "\ue03b", Description = "Put it into a list you made" },
        };

        // Only a playlist page can take a track out of its list.
        var removeCommand = (page.BindingContext as IPlaylistTrackContext)?.RemoveTrackCommand;
        var removeIndex = -1;
        if (removeCommand is not null)
        {
            removeIndex = actions.Count;
            actions.Add(new AppSheetAction
            {
                Label = "Remove from playlist",
                Glyph = "\ue15c",
                IsDestructive = true,
                Description = "Take it out of this list only",
            });
        }

        var tagIndex = actions.Count;
        actions.Add(new AppSheetAction { Label = "Edit tags", Glyph = "\ue3c9", Description = "Title, artist, album, cover and more" });

        var deleteIndex = actions.Count;
        actions.Add(new AppSheetAction { Label = "Delete from device", Glyph = "\ue872", IsDestructive = true, Description = "Permanently remove the audio file" });

        var choice = await AppDialogs.ShowActionsAsync(page, track.Name, actions, track.ArtistName);
        if (choice < 0)
        {
            return;
        }

        if (choice == removeIndex)
        {
            if (removeCommand!.CanExecute(track))
            {
                removeCommand.Execute(track);
                Haptics.Tap();
            }

            return;
        }

        if (choice == tagIndex)
        {
            OpenTagEditor(track);
            return;
        }

        if (choice == deleteIndex)
        {
            await DeleteTrackAsync(page, track);
            return;
        }

        switch (choice)
        {
            case 0:
                PlayTrack(track);
                break;
            case 1:
                PlayerService.Instance.AddToQueue(track);
                break;
            case 2:
                await AddToPlaylistAsync(page, track);
                break;
        }
    }

    /// <summary>
    /// "Add to playlist": picks an existing list (or creates one right there), because an
    /// empty library of lists would otherwise be a dead end.
    /// </summary>
    private async Task AddToPlaylistAsync(Page page, Music track)
    {
        var library = ServiceHelper.GetRequiredService<LibraryService>();

        var actions = library.Playlists
            .Select(playlist => new AppSheetAction
            {
                Label = playlist.Name,
                Glyph = "\ue03d",
                Description = $"{playlist.TrackCount} {(playlist.TrackCount == 1 ? "track" : "tracks")}",
                IsSelected = playlist.Tracks.Any(entry =>
                    ReferenceEquals(entry.Music, track) ||
                    string.Equals(entry.Music?.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase)),
            })
            .ToList();

        actions.Add(new AppSheetAction
        {
            Label = "New playlist…",
            Glyph = "\ue145",
            Description = "Start a fresh list and add this song",
        });

        var choice = await AppDialogs.ShowActionsAsync(page, "Add to playlist", actions, track.Name);
        if (choice < 0)
        {
            return;
        }

        Playlist target;

        if (choice >= library.Playlists.Count)
        {
            var name = await AppDialogs.PromptAsync(page, "New playlist", "Create", "Name it after a mood, a night, a place…");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            target = library.CreatePlaylist(name);
            library.AddTrackToPlaylist(target, track);
            await AppDialogs.ShowAlertAsync(page, "Added", $"\"{track.Name}\" starts \"{target.Name}\".", "OK");
            return;
        }

        target = library.Playlists[choice];

        if (library.AddTrackToPlaylist(target, track))
        {
            await AppDialogs.ShowAlertAsync(page, "Added", $"\"{track.Name}\" is now in \"{target.Name}\".", "OK");
        }
        else
        {
            await AppDialogs.ShowAlertAsync(page, "Already there", $"\"{track.Name}\" is already in \"{target.Name}\".", "OK");
        }
    }

    private void OpenTagEditor(Music track)
    {
        var session = ServiceHelper.GetRequiredService<TagEditorSession>();
        session.Track = track;

        var shell = Shell.Current;
        if (shell is not null)
        {
            _ = shell.GoToAsync(Routes.TagEditor);
        }
    }

    private async Task DeleteTrackAsync(Page page, Music track)
    {
        var fileService = ServiceHelper.GetRequiredService<MusicFileService>();

        // Android needs "All files access" to delete files by path; offer it before failing.
        if (!await fileService.EnsureFullFileAccessAsync(page, "delete music files"))
        {
            return;
        }

        var confirmed = await AppDialogs.ConfirmAsync(
            page,
            "Delete file?",
            $"\"{track.Name}\" will be permanently deleted from this device. This cannot be undone.",
            "Delete",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        try
        {
            await fileService.DeleteTrackAsync(track);
        }
        catch (Exception ex)
        {
            await page.DisplayAlert("Delete failed", ex.Message, "OK");
        }
    }
}
