using Application.Services;
using Domain.Interfaces;
using Domain.Models;
using Infrastructure.Persistence;

namespace MusicPlayer.UI.Services;

/// <summary>
/// File-level operations the UI triggers from menus: saving edited tags, deleting audio
/// files and writing lyrics into a file. Every operation re-reads the file afterwards,
/// updates the shared <see cref="LibraryService"/> and persists the library snapshot, so the
/// UI and the saved library never drift from what is on disk.
/// </summary>
public sealed class MusicFileService
{
    private readonly IMetadataWriter _writer;
    private readonly IMetadataReader _reader;
    private readonly IArtworkThumbnailer _thumbnailer;
    private readonly LibraryService _library;
    private readonly LibraryJsonStore _snapshotStore;
    private readonly LyricsService _lyrics;
    private readonly IStorageAccessService _storage;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

    public MusicFileService(
        IMetadataWriter writer,
        IMetadataReader reader,
        IArtworkThumbnailer thumbnailer,
        LibraryService library,
        LibraryJsonStore snapshotStore,
        LyricsService lyrics,
        IStorageAccessService storage)
    {
        _writer = writer;
        _reader = reader;
        _thumbnailer = thumbnailer;
        _library = library;
        _snapshotStore = snapshotStore;
        _lyrics = lyrics;
        _storage = storage;
    }

    /// <summary>
    /// Verifies full file access and, when missing, offers to open the system settings page.
    /// Returns true only when the caller may proceed right now.
    /// </summary>
    public async Task<bool> EnsureFullFileAccessAsync(Page? page, string action)
    {
        if (_storage.HasFullFileAccess)
        {
            return true;
        }

        if (page is not null)
        {
            var open = await page.DisplayAlert(
                "File access needed",
                $"Android needs \"All files access\" so the app can {action}. Turn on \"Allow management of all files\", then come back and try again.",
                "Open settings",
                "Not now");

            if (open)
            {
                await _storage.RequestFullFileAccessAsync();
            }
        }

        return false;
    }

    /// <summary>Writes the edit into the audio file's tags and refreshes the library entry.</summary>
    public async Task<Music> SaveTagsAsync(Music track, MusicTagEdit edit)
    {
        await Task.Run(() => _writer.Write(track.FilePath, edit));

        var refreshed = await Task.Run(() => _reader.Read(track.FilePath)) ?? track;

        // Library-only information the file itself cannot restore.
        refreshed.DateAdded = track.DateAdded;
        refreshed.LastPlayedAt = track.LastPlayedAt;

        // A new cover was just embedded: rebuild its list thumbnail so the UI keeps binding
        // the small image (the scanner only does this during a full scan).
        if (refreshed.Artwork is { } artwork)
        {
            await Task.Run(() => _thumbnailer.EnsureThumbnail(artwork));
        }

        _library.ReplaceTrack(track, refreshed);
        PlayerService.Instance.HandleTrackReplaced(track, refreshed);
        await SaveSnapshotAsync();
        return refreshed;
    }

    /// <summary>Writes lyrics text (plain or LRC) into the file's embedded lyrics tag.</summary>
    public Task<Music> SaveLyricsToFileAsync(Music track, string lyricsText)
        => SaveTagsAsync(track, new MusicTagEdit { LyricsText = lyricsText });

    /// <summary>Deletes the audio file, then removes the track everywhere it is referenced.</summary>
    public async Task DeleteTrackAsync(Music track)
    {
        await Task.Run(() =>
        {
            if (File.Exists(track.FilePath))
            {
                File.Delete(track.FilePath);
            }
        });

        _lyrics.DeleteCachedLyrics(track);
        PlayerService.Instance.HandleTrackDeleted(track);
        _library.RemoveTrack(track);
        await SaveSnapshotAsync();
    }

    /// <summary>Persists the current library so the next launch starts without a scan.</summary>
    public async Task SaveSnapshotAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            await _snapshotStore.SaveAsync(_library.CreateSnapshot());
        }
        catch (IOException)
        {
            // Persistence is an optimization; a failed save must never break the edit itself.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _snapshotLock.Release();
        }
    }
}
