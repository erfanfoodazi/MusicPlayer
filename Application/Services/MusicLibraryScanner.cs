using System.IO;
using Domain;
using Domain.Interfaces;
using Domain.Models;

namespace Application.Services;

/// <summary>One file that could not be read; the scan continues instead of failing (spec section 12).</summary>
public record ScanError(string FilePath, string Message);

public record ScanResult(IReadOnlyList<Music> Tracks, IReadOnlyList<ScanError> Errors);

/// <summary>
/// Scans a folder tree for supported audio files and turns them into <see cref="Music"/>
/// models via the injected <see cref="IMetadataReader"/>.
///
/// This is use-case level code (spec section 10): it knows about folders, cancellation and
/// progress, but not about MAUI, TagLib, or the UI.
/// </summary>
public class MusicLibraryScanner
{
    private readonly IMetadataReader _metadataReader;
    private readonly IArtworkThumbnailer _artworkThumbnailer;

    public MusicLibraryScanner(IMetadataReader metadataReader, IArtworkThumbnailer artworkThumbnailer)
    {
        _metadataReader = metadataReader;
        _artworkThumbnailer = artworkThumbnailer;
    }

    /// <param name="folderPath">Root folder to scan recursively.</param>
    /// <param name="progress">Optional progress messages, safe to bind to UI (marshals to UI thread).</param>
    /// <param name="ct">Cancels a long scan cleanly (spec section 12).</param>
    public async Task<ScanResult> ScanAsync(
        string folderPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Music folder not found: {folderPath}");
        }

        var files = EnumerateAudioFiles(folderPath, ct);
        return await ScanFilesAsync(files, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a known list of audio files. Android supplies this list from MediaStore, which
    /// includes music in Download, messaging apps and removable volumes without walking the
    /// entire filesystem.
    /// </summary>
    public async Task<ScanResult> ScanFilesAsync(
        IReadOnlyList<string> candidateFiles,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var files = candidateFiles
            .Where(path => !string.IsNullOrWhiteSpace(path) && SupportedAudioFormats.IsSupported(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tracks = new List<Music>(files.Count);
        var errors = new List<ScanError>();

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[i];

            // Metadata reads parse the file header and can be slow on big FLACs - keep them
            // off the UI thread, one file at a time so memory stays predictable. Thumbnail
            // creation also stays here so the UI never decodes a multi-megabyte cover.
            var music = await Task.Run(() =>
            {
                var track = TryRead(file, errors);

                if (track?.Artwork is not null)
                {
                    _artworkThumbnailer.EnsureThumbnail(track.Artwork, ct);
                }

                return track;
            }, ct);

            if (music is not null)
            {
                tracks.Add(music);
            }

            progress?.Report($"Scanning {i + 1}/{files.Count}: {Path.GetFileName(file)}");
        }

        return new ScanResult(LibraryGrouping.Build(tracks), errors);
    }

    /// <summary>
    /// Walks the folder tree manually instead of using SearchOption.AllDirectories, because
    /// the built-in enumeration throws on the FIRST inaccessible subfolder and aborts the
    /// whole scan (spec sections 4/12: inaccessible files must be handled gracefully).
    /// Reparse points (junctions/symlinks) are skipped to avoid cycles.
    /// Public so the startup check can compare the folder's current files against the saved
    /// snapshot without paying for a metadata scan.
    /// </summary>
    public static List<string> EnumerateAudioFiles(string root, CancellationToken ct)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue; // skip folders we may not read; the rest of the scan continues
            }
            catch (DirectoryNotFoundException)
            {
                continue; // folder vanished mid-scan (external drive unplugged, etc.)
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue; // junction/symlink: could loop back to a parent
                        }

                        pending.Push(entry);
                    }
                    else if (SupportedAudioFormats.IsSupported(entry))
                    {
                        result.Add(entry);
                    }
                }
                catch
                {
                    // A single inaccessible entry must not abort the walk either.
                }
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private Music? TryRead(string file, List<ScanError> errors)
    {
        try
        {
            return _metadataReader.Read(file);
        }
        catch (Exception ex)
        {
            // One corrupt or DRM-protected file must never abort the whole scan.
            errors.Add(new ScanError(file, ex.Message));
            return null;
        }
    }

}
