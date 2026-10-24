using Domain.Interfaces;
using Domain.Models;
using TFile = TagLib.File;

namespace Infrastructure.Metadata;

/// <summary>
/// Writes user-edited tags with TagLibSharp. Only fields present on <see cref="MusicTagEdit"/>
/// are touched, so an editor screen can save a subset without clobbering tags it does not show.
/// </summary>
public class TagLibMetadataWriter : IMetadataWriter
{
    public void Write(string filePath, MusicTagEdit edit)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("No file path was supplied.");
        }

        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"File not found: {filePath}");
        }

        try
        {
            using var tagFile = TFile.Create(filePath);
            var tag = tagFile.Tag;

            if (edit.Title is not null)
            {
                tag.Title = edit.Title.Trim();
            }

            if (edit.Artist is not null)
            {
                tag.Performers = SplitTagValues(edit.Artist);
            }

            if (edit.Album is not null)
            {
                tag.Album = edit.Album.Trim();
            }

            if (edit.AlbumArtist is not null)
            {
                tag.AlbumArtists = SplitTagValues(edit.AlbumArtist);
            }

            if (edit.Year is { } year)
            {
                tag.Year = (uint)Math.Clamp(year, 0, 9999);
            }

            if (edit.TrackNumber is { } track)
            {
                tag.Track = (uint)Math.Clamp(track, 0, 99999);
            }

            if (edit.DiscNumber is { } disc)
            {
                tag.Disc = (uint)Math.Clamp(disc, 0, 99999);
            }

            if (edit.Genres is not null)
            {
                tag.Genres = edit.Genres
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .ToArray();
            }

            if (edit.LyricsText is not null)
            {
                tag.Lyrics = edit.LyricsText;
            }

            if (edit.CoverImageBytes is { Length: > 0 })
            {
                tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(edit.CoverImageBytes))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = string.IsNullOrWhiteSpace(edit.CoverMimeType) ? "image/jpeg" : edit.CoverMimeType,
                        Description = "Cover",
                    },
                ];
            }
            else if (edit.RemoveCover)
            {
                tag.Pictures = [];
            }

            tagFile.Save();
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            throw new InvalidOperationException(FriendlyWriteMessage(ex), ex);
        }
    }

    private static bool IsWriteFailure(Exception ex)
        => ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException
            or ArgumentException;

    private static string FriendlyWriteMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            "The file could not be written. On Android, allow all-files access in Settings, then try again.",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) =>
            "The file is in use by another app. Close it and try again.",
        IOException => "The file could not be written (storage error).",
        TagLib.CorruptFileException or TagLib.UnsupportedFormatException =>
            "This file's format does not support tag editing.",
        _ => $"Could not save tags: {ex.Message}",
    };

    private static string[] SplitTagValues(string value)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
