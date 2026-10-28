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

    public Music? Read(string filePath)
    {
        // FileShare.Read lets us read files that another process (e.g. a sync tool) has open.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var music = ReadCore(stream, Path.GetFileName(filePath), filePath);

        if (music is not null)
        {
            try
            {
                music.DateModified = File.GetLastWriteTimeUtc(filePath);
            }
            catch
            {
                // A stat failure only costs the "date modified" sort for this file.
            }
        }

        return music;
    }

    public Music? Read(Stream stream, string nameHint)
        => ReadCore(stream, nameHint, nameHint);

    private Music? ReadCore(Stream stream, string nameHint, string filePath)
    {
        // TagLib identifies the format from the name hint's extension and parses the
        // container from the stream. It needs a seekable stream (all formats rewind).
        // Downloads often carry the wrong extension (an AAC-in-MP4 file named .mp3), so the
        // real container is sniffed first and passed explicitly.
        var mimeType = DetectMimeType(stream, nameHint);
        using var tagFile = TFile.Create(
            new ReadOnlyStreamAbstraction(nameHint, stream),
            mimeType,
            TagLib.ReadStyle.Average);

        var tag = tagFile.Tag;
        var props = tagFile.Properties;

        // Skip files that contain video tracks (real videos, not audio-only MP4/M4A files).
        // TagLib reports video dimensions when a video track is present.
        if (props.VideoWidth > 0 || props.VideoHeight > 0)
        {
            return null;
        }

        var title = tag.Title?.Trim();
        var performer = tag.Performers.FirstOrDefault()?.Trim() ?? string.Empty;
        var albumArtist = tag.AlbumArtists.FirstOrDefault()?.Trim();
        var albumTitle = tag.Album?.Trim();

        var music = new Music
        {
            FilePath = filePath,
            Format = GetFormatName(nameHint, mimeType),
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
                Codec = string.IsNullOrWhiteSpace(props.Description) ? GetFormatName(nameHint, mimeType) : props.Description,
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

    /// <summary>
    /// Identifies the real container from its magic bytes. Files shared through messaging
    /// apps and download sites regularly keep a misleading extension, and TagLib would pick
    /// the wrong parser from that extension alone.
    /// </summary>
    private static string? DetectMimeType(Stream stream, string nameHint)
    {
        if (!stream.CanSeek)
        {
            return MimeTypeForExtension(nameHint);
        }

        var position = stream.Position;
        Span<byte> header = stackalloc byte[12];
        var read = stream.Read(header);
        stream.Position = position;

        if (read >= 3 && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            return "audio/mpeg";
        }

        if (read >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' &&
            header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            return "audio/mp4";
        }

        if (read >= 4 && header[0] == (byte)'f' && header[1] == (byte)'L' &&
            header[2] == (byte)'a' && header[3] == (byte)'C')
        {
            return "audio/flac";
        }

        if (read >= 4 && header[0] == (byte)'O' && header[1] == (byte)'g' &&
            header[2] == (byte)'g' && header[3] == (byte)'S')
        {
            return "audio/ogg";
        }

        if (read >= 4 && header[0] == (byte)'R' && header[1] == (byte)'I' &&
            header[2] == (byte)'F' && header[3] == (byte)'F')
        {
            return "audio/x-wav";
        }

        if (read >= 4 && header[0] == (byte)'F' && header[1] == (byte)'O' &&
            header[2] == (byte)'R' && header[3] == (byte)'M')
        {
            return "audio/aiff";
        }

        if (read >= 4 && header[0] == (byte)'M' && header[1] == (byte)'A' &&
            header[2] == (byte)'C' && header[3] == (byte)' ')
        {
            return "audio/x-ape";
        }

        if (read >= 2 && header[0] == 0xFF)
        {
            // ADTS sync: layer bits are 00, which separates AAC from MPEG audio frames.
            if ((header[1] & 0xF6) == 0xF0)
            {
                return "audio/aac";
            }

            if ((header[1] & 0xE0) == 0xE0)
            {
                return "audio/mpeg";
            }
        }

        return MimeTypeForExtension(nameHint);
    }

    private static string? MimeTypeForExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/x-wav",
        ".aac" => "audio/aac",
        ".m4a" or ".mp4" or ".alac" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".aif" or ".aiff" => "audio/aiff",
        ".ape" => "audio/x-ape",
        ".wma" => "audio/x-ms-wma",
        _ => null,
    };

    private static string GetFormatName(string fileName, string? mimeType)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();

        return mimeType switch
        {
            "audio/mpeg" => "MP3",
            "audio/flac" => "FLAC",
            "audio/x-wav" => "WAV",
            "audio/aac" => "AAC",
            "audio/mp4" => extension == "MP4" || extension == "ALAC" ? extension : "M4A",
            "audio/ogg" => extension == "OPUS" ? "OPUS" : "OGG",
            "audio/aiff" => "AIFF",
            "audio/x-ape" => "APE",
            "audio/x-ms-wma" => "WMA",
            _ => extension,
        };
    }

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
