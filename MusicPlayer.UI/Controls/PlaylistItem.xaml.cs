using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Controls;

public partial class PlaylistItem : Grid
{
    public static readonly BindableProperty PlaylistProperty =
        BindableProperty.Create(nameof(Playlist), typeof(Playlist), typeof(PlaylistItem));

    public Playlist? Playlist
    {
        get => (Playlist?)GetValue(PlaylistProperty);
        set => SetValue(PlaylistProperty, value);
    }

    public PlaylistItem()
    {
        InitializeComponent();
    }

    private void OnPlayClicked(object? sender, EventArgs e)
    {
        if (Playlist is null)
        {
            return;
        }

        var tracks = Playlist.Tracks
            .Select(track => track.Music)
            .OfType<Music>()
            .ToList();

        if (tracks.Count == 0)
        {
            return;
        }

        Haptics.Tap();
        PlayerService.Instance.SetQueue(tracks);
    }

    private async void OnPlaylistTapped(object? sender, TappedEventArgs e)
    {
        if (Playlist is null) return;

        Haptics.Tap();
        _ = Motion.PressAsync(this);

        var shell = Shell.Current;
        if (shell is not null)
        {
            await shell.GoToAsync($"{Routes.PlaylistDetail}?playlistId={Playlist.Id}");
        }
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        if (Playlist is null) return;

        var page = Shell.Current?.CurrentPage;
        if (page is null) return;

        var menuShell = Shell.Current;
        var choice = await AppDialogs.ShowActionsAsync(
            page,
            Playlist.Name,
            [
                new AppSheetAction { Label = "Add songs", Glyph = "\ue145", Description = "Pick several tracks and add them at once" },
                new AppSheetAction { Label = "Play", Glyph = "\ue037", Description = "Start from the first track" },
                new AppSheetAction { Label = "Shuffle", Glyph = "\ue043", Description = "Play in a random order" },
                new AppSheetAction { Label = "Change cover", Glyph = "\ue412", Description = "Use a picture from this device" },
                new AppSheetAction { Label = "Reset cover", Glyph = "\ue166", Description = "Go back to the first song's artwork" },
                new AppSheetAction { Label = "Rename", Glyph = "\ue3c9", Description = "Give this list a new name" },
                new AppSheetAction { Label = "Delete playlist", Glyph = "\ue872", IsDestructive = true, Description = "Remove the list only; files stay on the device" },
            ],
            Playlist.TrackCount == 0 ? "Empty list" : $"{Playlist.TrackCount} songs");

        var library = ServiceHelper.GetRequiredService<LibraryService>();

        switch (choice)
        {
            case 0:
                if (menuShell is not null)
                {
                    await menuShell.GoToAsync($"{Routes.AddSongs}?playlistId={Playlist.Id}");
                }

                return;
            case 3:
                await PlaylistArtworkService.PickAndApplyAsync(page, Playlist, library);
                return;
            case 4:
                PlaylistArtworkService.Reset(Playlist, library);
                return;
            case 5:
                await RenameAsync(page, library);
                return;
            case 6:
                await DeleteAsync(page, library);
                return;
        }

        if (choice is not (1 or 2))
        {
            return;
        }

        var tracks = Playlist.Tracks.Select(t => t.Music).OfType<Music>().ToList();
        if (tracks.Count == 0)
        {
            await AppDialogs.ShowAlertAsync(
                page,
                "Playlist is empty",
                "Use \"Add songs\" on this list, or a track's \u22ee menu, to fill it.",
                "OK");
            return;
        }

        if (menuShell is null)
        {
            return;
        }

        if (choice == 1)
        {
            PlayerService.Instance.SetQueue(tracks);
        }
        else
        {
            PlayerService.Instance.SetQueue(tracks.OrderBy(_ => Guid.NewGuid()));
        }

        await menuShell.GoToAsync(Routes.NowPlaying);
    }

    private async Task RenameAsync(Page page, LibraryService library)
    {
        if (Playlist is null)
        {
            return;
        }

        var name = await AppDialogs.PromptAsync(page, "Rename playlist", "Save", null, Playlist.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            library.RenamePlaylist(Playlist, name);
        }
    }

    private async Task DeleteAsync(Page page, LibraryService library)
    {
        if (Playlist is null)
        {
            return;
        }

        var confirmed = await AppDialogs.ConfirmAsync(
            page,
            "Delete playlist?",
            $"\"{Playlist.Name}\" and its order will be removed. The audio files stay on this device.",
            "Delete",
            "Cancel");

        if (confirmed)
        {
            library.DeletePlaylist(Playlist);
        }
    }
}
