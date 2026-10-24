using System.Security.Cryptography;
using Domain.Interfaces;
using Domain.Models;
using TFile = TagLib.File;
using LyricsModel = Domain.Models.Lyrics;

namespace Infrastructure.Metadata;

/// <summary>
/// Reads audio metadata using TagLibSharp (verified netstandard2.0, compatible with both
/// net9.0-android and net9.0-windows).
///
/// Two entry points exist because the two platforms expose files differently:
///  - Windows: a normal filesystem path.
///  - Android: content URIs exposed as streams (Storage Access Framework) - the stream-based
///    overload is what the Android scanner will use in the next step.
/// </summary>
public class TagLibMetadataReader : IMetadataReader
{
    private readonly string _artworkCacheDirectory;

    /// <param name="artworkCacheDirectory">Where extracted cover images are written (pass an app cache subfolder).</param>
    public TagLibMetadataReader(string artworkCacheDirectory)
    {
        _artworkCacheDirectory = artworkCacheDirectory;
        Directory.CreateDirectory(_artworkCacheDirectory);
    }

    public Music Read(string filePath)
    {
        // FileShare.Read lets us read files that another process (e.g. a sync tool) has open.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCore(stream, Path.GetFileName(filePath), filePath);
    }

    public Music Read(Stream stream, string nameHint)
        => ReadCore(stream, nameHint, nameHint);

    private Music ReadCore(Stream stream, string nameHint, string filePath)
    {
        // TagLib identifies the format from the name hint's extension, then parses the
        // container from the stream. It needs a seekable stream (all formats rewind).
        using var tagFile = TFile.Create(new ReadOnlyStreamAbstraction(nameHint, stream));

        var tag = tagFile.Tag;
        var props = tagFile.Properties;

        var title = tag.Title?.Trim();
        var performer = tag.Performers.FirstOrDefault()?.Trim() ?? string.Empty;
        var albumArtist = tag.AlbumArtists.FirstOrDefault()?.Trim();
        var albumTitle = tag.Album?.Trim();

        var music = new Music
        {
            FilePath = filePath,
            Format = GetFormatName(nameHint),
            FileSize = GetStreamLength(stream),
            Duration = props.Duration,

            // Fall back to the file name when a file has no title tag (spec section 4:
            // handle missing metadata gracefully instead of showing empty rows).
            Name = !string.IsNullOrEmpty(title) ? title : Path.GetFileNameWithoutExtension(nameHint),

            AlbumArtist = !string.IsNullOrEmpty(albumArtist) ? albumArtist : performer,
            Year = tag.Year > 0 ? (int)tag.Year : null,
            TrackNumber = tag.Track > 0 ? (int)tag.Track : null,
            DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null,

            Genres = tag.Genres
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => new Genre { Name = g.Trim() })
                .ToList(),

            AudioFileInfo = new AudioFileInfo
            {
                Codec = string.IsNullOrWhiteSpace(props.Description) ? GetFormatName(nameHint) : props.Description,
                BitrateKbps = (int)props.AudioBitrate,
                SampleRateHz = (int)props.AudioSampleRate,
                Channels = props.AudioChannels,
                HasEmbeddedArtwork = tag.Pictures.Length > 0,
            },
        };

        // Minimal navigation objects; the scanner afterwards groups tracks into shared
        // Artist/Album instances so the Artists and Albums tabs get real data too.
        if (!string.IsNullOrEmpty(performer))
        {
            music.Artist = new Artist { Name = performer };
        }

        if (!string.IsNullOrEmpty(albumTitle))
        {
            music.Album = new Album
            {
                Title = albumTitle,
                AlbumArtist = music.AlbumArtist,
                Year = music.Year,
            };
        }

        // Extract embedded artwork to the cache so UI can bind a file path instead of
        // holding image bytes in memory (spec section 12).
        var picture = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                      ?? tag.Pictures.FirstOrDefault();
        if (picture is not null && picture.Data.Count > 0)
        {
            music.Artwork = ExtractArtwork(picture);
        }

        // Unsynchronized embedded lyrics (ID3 USLT, Vorbis LYRICS, MP4 ©lyr...). If the
        // text actually contains LRC timestamps, LyricsService parses it into lines.
        var embeddedLyrics = tag.Lyrics;
        if (!string.IsNullOrWhiteSpace(embeddedLyrics))
        {
            music.Lyrics = new LyricsModel
            {
                Type = LyricsType.Plain,
                Source = LyricsSource.EmbeddedMetadata,
                Text = embeddedLyrics.Trim(),
            };
        }

        return music;
    }

    private Artwork ExtractArtwork(TagLib.IPicture picture)
    {
        var bytes = picture.Data.Data;
        var extension = ExtensionForMimeType(picture.MimeType);
        // Content-hash the file name: identical covers are stored once, and re-scans
        // overwrite the same file instead of piling up duplicates.
        var hash = Convert.ToHexString(SHA1.HashData(bytes))[..16];
        var cachePath = Path.Combine(_artworkCacheDirectory, hash + extension);

        if (!File.Exists(cachePath))
        {
            File.WriteAllBytes(cachePath, bytes);
        }

        return new Artwork
        {
            FilePath = cachePath,
            MimeType = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType,
            FileSize = bytes.LongLength,
            Source = ArtworkSource.Embedded,
        };
    }

    private static string ExtensionForMimeType(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    private static string GetFormatName(string fileName)
        => Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();

    private static long GetStreamLength(Stream stream)
        => stream.CanSeek ? stream.Length : 0;

    /// <summary>
    /// Adapts a .NET stream to TagLib's file abstraction. Read-only on purpose: metadata
    /// WRITING (spec section 7) gets its own carefully-designed path later - this step only
    /// reads, and a guard on WriteStream makes accidental writes impossible.
    /// </summary>
    private sealed class ReadOnlyStreamAbstraction : TagLib.File.IFileAbstraction
    {
        private readonly Stream _stream;

        public ReadOnlyStreamAbstraction(string name, Stream stream)
        {
            Name = name;
            _stream = stream;
        }

        public string Name { get; }

        public Stream ReadStream => _stream;

        public Stream WriteStream
            => throw new InvalidOperationException("This abstraction is read-only; metadata writing is not implemented yet.");

        public void CloseStream(Stream stream)
        {
            // Ownership of the stream stays with the caller (the using directives above);
            // TagLib must not close it underneath us.
        }
    }
}
