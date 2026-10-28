using Domain.Models;
using MusicPlayer.UI.Controls;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;

namespace MusicPlayer.UI.Views;

public partial class PlaylistDetailPage : ContentPage, IQueryAttributable
{
    private int _playlistId;
    private bool _introPlayed;
    private bool _pickingCover;
    private Music? _draggedTrack;
    private VisualElement? _draggedRow;

    public PlaylistDetailPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<PlaylistDetailViewModel>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("playlistId", out var value) &&
            int.TryParse(value?.ToString(), out var id))
        {
            _playlistId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        ResetDraggedRowOpacity();

        if (BindingContext is PlaylistDetailViewModel viewModel && _playlistId > 0)
        {
            viewModel.SetPlaylistId(_playlistId);
            await viewModel.LoadPlaylistCommand.ExecuteAsync(null);

            if (!_introPlayed)
            {
                _introPlayed = true;

                Motion.Prepare(TopBar, offsetY: -8);
                Motion.Prepare(PlaylistContent, offsetY: 20);

                await Task.Delay(16);
                await Motion.IntroAsync(0, 70, TopBar, PlaylistContent);
            }
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async void OnPlaylistMenuClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not PlaylistDetailViewModel viewModel || viewModel.Playlist is null)
        {
            return;
        }

        var playlist = viewModel.Playlist;

        var choice = await AppDialogs.ShowActionsAsync(
            this,
            playlist.Name,
            [
                new AppSheetAction { Label = "Add songs", Glyph = "\ue145", Description = "Pick several tracks and add them at once" },
                new AppSheetAction { Label = "Change cover", Glyph = "\ue412", Description = "Use a picture from this device" },
                new AppSheetAction { Label = "Reset cover", Glyph = "\ue166", Description = "Go back to the first song's artwork" },
                new AppSheetAction { Label = "Rename", Glyph = "\ue3c9", Description = "Give this list a new name" },
                new AppSheetAction { Label = "Delete playlist", Glyph = "\ue872", IsDestructive = true, Description = "Remove the list only; files stay on the device" },
            ],
            playlist.TrackCount == 0 ? "Empty list" : $"{playlist.TrackCount} songs");

        switch (choice)
        {
            case 0:
                await viewModel.AddSongsCommand.ExecuteAsync(null);
                break;

            case 1:
                var library = ServiceHelper.GetRequiredService<LibraryService>();
                await PlaylistArtworkService.PickAndApplyAsync(this, playlist, library);
                viewModel.NotifyArtworkChanged();
                break;

            case 2:
                PlaylistArtworkService.Reset(playlist, ServiceHelper.GetRequiredService<LibraryService>());
                viewModel.NotifyArtworkChanged();
                break;

            case 3:
                var name = await AppDialogs.PromptAsync(this, "Rename playlist", "Save", null, playlist.Name);
                if (!string.IsNullOrWhiteSpace(name) && viewModel.RenamePlaylist(name))
                {
                    Haptics.Tap();
                }

                break;

            case 4:
                var confirmed = await AppDialogs.ConfirmAsync(
                    this,
                    "Delete playlist?",
                    $"\"{playlist.Name}\" and its order will be removed. The audio files stay on this device.",
                    "Delete",
                    "Cancel");

                if (confirmed && viewModel.DeletePlaylist())
                {
                    await Shell.Current.GoToAsync("..");
                }

                break;
        }
    }

    private void OnCoverTapped(object? sender, TappedEventArgs e) => OnChangeCoverClicked(sender, EventArgs.Empty);

    private async void OnChangeCoverClicked(object? sender, EventArgs e)
    {
        if (_pickingCover ||
            BindingContext is not PlaylistDetailViewModel viewModel ||
            viewModel.Playlist is not { } playlist)
        {
            return;
        }

        _pickingCover = true;

        try
        {
            await PlaylistArtworkService.PickAndApplyAsync(
                this,
                playlist,
                ServiceHelper.GetRequiredService<LibraryService>());
            viewModel.NotifyArtworkChanged();
        }
        finally
        {
            _pickingCover = false;
        }
    }

    private void ResetDraggedRowOpacity() => ResetRowOpacity(_draggedRow);

    /// <summary>
    /// Restores one specific row. The 4 s fallback must not clear the state of a newer drag
    /// that started in the meantime, so the row is passed in rather than read from the field.
    /// </summary>
    private void ResetRowOpacity(VisualElement? row)
    {
        if (row is null)
        {
            return;
        }

        row.Opacity = 1;

        if (ReferenceEquals(_draggedRow, row))
        {
            _draggedRow = null;
        }
    }

    /// <summary>
    /// Android starts a drag after a long press. The row lifts slightly while it travels and
    /// drops into the position of the row underneath it.
    /// </summary>
    private void OnTrackDragStarting(object? sender, DragStartingEventArgs e)
    {
        _draggedTrack = (sender as TrackItem)?.Track;

        if (sender is VisualElement element)
        {
            ResetDraggedRowOpacity();
            _draggedRow = element;
            element.Opacity = 0.45;

            // Android does not always deliver DropCompleted when a drag is cancelled, which
            // used to leave rows permanently dimmed ("faded"). Restore on a timer as well,
            // and only for the row that was dimmed here.
            var row = element;
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(4), () => ResetRowOpacity(row));
        }
    }

    private void OnTrackDragCompleted(object? sender, DropCompletedEventArgs e)
    {
        ResetDraggedRowOpacity();
        _draggedTrack = null;
    }

    private void OnTrackDrop(object? sender, DropEventArgs e)
    {
        ResetDraggedRowOpacity();

        var target = (sender as TrackItem)?.Track;
        var dragged = _draggedTrack;
        _draggedTrack = null;

        if (dragged is null || target is null || ReferenceEquals(dragged, target))
        {
            return;
        }

        if (BindingContext is PlaylistDetailViewModel viewModel && viewModel.MoveTrack(dragged, target))
        {
            Haptics.Tap();
        }
    }
}
