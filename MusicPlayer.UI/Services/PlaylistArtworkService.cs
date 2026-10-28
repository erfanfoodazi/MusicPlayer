using Domain.Models;
using Microsoft.Maui.Storage;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Picks a cover image for a playlist and stores a private copy, so the cover keeps working
/// even when the original photo is moved or deleted. The playlist model only keeps a path;
/// the copy lives next to the app data and is removed when the user resets the cover.
/// </summary>
public static class PlaylistArtworkService
{
    private static string CoverDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, "playlist-covers");

    public static async Task PickAndApplyAsync(Page page, Playlist playlist, LibraryService library)
    {
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = $"Cover for {playlist.Name}",
            });

            if (photo is null)
            {
                return;
            }

            Directory.CreateDirectory(CoverDirectory);

            var extension = Path.GetExtension(photo.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var destination = Path.Combine(
                CoverDirectory,
                $"playlist-{playlist.Id}-{DateTime.UtcNow.Ticks}{extension}");

            await using (var source = await photo.OpenReadAsync())
            await using (var target = File.Create(destination))
            {
                await source.CopyToAsync(target);
            }

            DeleteOwnedCover(playlist.ArtworkPath);
            library.SetPlaylistArtwork(playlist, destination);
            Haptics.Tap();
        }
        catch (PermissionException)
        {
            await AppDialogs.ShowAlertAsync(
                page,
                "Permission needed",
                "Allow photo access so Auralis can read the picture you pick.",
                "OK");
        }
        catch (FeatureNotSupportedException)
        {
            await AppDialogs.ShowAlertAsync(
                page,
                "Not available",
                "This device does not offer a photo picker.",
                "OK");
        }
        catch (Exception ex)
        {
            await AppDialogs.ShowAlertAsync(page, "Could not set cover", ex.Message, "OK");
        }
    }

    /// <summary>Drops the custom cover and goes back to the first track's artwork.</summary>
    public static void Reset(Playlist playlist, LibraryService library)
    {
        DeleteOwnedCover(playlist.ArtworkPath);
        library.SetPlaylistArtwork(playlist, null);
        Haptics.Tap();
    }

    /// <summary>Only removes files this service created; library artwork is left untouched.</summary>
    private static void DeleteOwnedCover(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var coverRoot = Path.GetFullPath(CoverDirectory);

            if (fullPath.StartsWith(coverRoot, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // A stale cover copy is harmless; never block the reset on cleanup.
        }
    }
}
