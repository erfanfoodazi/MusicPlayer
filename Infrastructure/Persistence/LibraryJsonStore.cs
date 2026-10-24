using System.Globalization;
using System.Text.Json;
using Domain.Models;
using LyricsModel = Domain.Models.Lyrics;

namespace Infrastructure.Persistence;

/// <summary>
/// Saves the scanned library to a small JSON file so the app no longer re-parses every audio
/// file on each launch. Serialization is written by hand with Utf8JsonWriter/JsonDocument
/// instead of reflection-based serializers, because reflection is trimmed away in Android
/// Release builds and would fail there silently.
///
/// The file is written to a temp file first and then moved over the old one, so a crash or a
/// full disk can never leave a half-written snapshot behind.
/// </summary>
public class LibraryJsonStore
{
    private const int CurrentVersion = 1;

    private readonly string _filePath;

    // Playlist edits each request a save. Without serialization two saves can race for the
    // same temp file, which silently loses the newest playlist change.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public LibraryJsonStore(string filePath)
    {
        _filePath = filePath;
    }

    public Task<LibrarySnapshot?> LoadAsync() => Task.Run(Load);

    public LibrarySnapshot? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_filePath));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                GetInt(root, "version") != CurrentVersion)
            {
                return null; // written by an older app version: rescan instead of guessing
            }

            var snapshot = new LibrarySnapshot
            {
                Version = CurrentVersion,
                FolderPath = GetString(root, "folderPath") ?? string.Empty,
                SavedAtUtc = GetDate(root, "savedAtUtc") ?? DateTime.MinValue,
            };

            if (root.TryGetProperty("filePaths", out var filePaths) && filePaths.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in filePaths.EnumerateArray())
                {
                    var path = element.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        snapshot.FilePaths.Add(path);
                    }
                }
            }

            if (root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in tracks.EnumerateArray())
                {
                    var track = ReadTrack(element);
                    if (track is not null && !string.IsNullOrWhiteSpace(track.FilePath))
                    {
                        snapshot.Tracks.Add(track);
                    }
                }
            }

            if (root.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in playlists.EnumerateArray())
                {
                    var name = GetString(element, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var playlist = new Playlist
                    {
                        Id = GetInt(element, "id") ?? 0,
                        Name = name,
                        CreatedAt = GetDate(element, "createdAtUtc") ?? DateTime.UtcNow,
                        UpdatedAt = GetDate(element, "updatedAtUtc") ?? DateTime.UtcNow,
                    };

                    if (element.TryGetProperty("tracks", out var playlistTracks) &&
                        playlistTracks.ValueKind == JsonValueKind.Array)
                    {
                        var order = 0;
                        foreach (var trackElement in playlistTracks.EnumerateArray())
                        {
                            var path = GetString(trackElement, "filePath");
                            if (string.IsNullOrWhiteSpace(path))
                            {
                                continue;
                            }

                            // MusicId is resolved from the file path after the library is
                            // published; ids are reassigned on every load.
                            playlist.Tracks.Add(new PlaylistTrack
                            {
                                Id = order + 1,
                                PlaylistId = playlist.Id,
                                Order = order,
                                Music = new Music { FilePath = path },
                            });
                            order++;
                        }
                    }

                    snapshot.Playlists.Add(playlist);
                }
            }

            return snapshot;
        }
        catch (JsonException)
        {
            return null; // corrupt file: fall back to a real scan
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(LibrarySnapshot snapshot)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => Save(snapshot)).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Save(LibrarySnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";

        using (var stream = File.Create(tempPath))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("folderPath", snapshot.FolderPath ?? string.Empty);
            writer.WriteString("savedAtUtc", snapshot.SavedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            // Every file the scan saw (readable or not) so the next launch can tell whether
            // the folder changed without parsing anything.
            writer.WriteStartArray("filePaths");
            foreach (var path in snapshot.FilePaths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("tracks");

            foreach (var track in snapshot.Tracks)
            {
                WriteTrack(writer, track);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("playlists");

            foreach (var playlist in snapshot.Playlists)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", playlist.Id);
                writer.WriteString("name", playlist.Name);
                writer.WriteString("createdAtUtc", playlist.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("updatedAtUtc", playlist.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

                writer.WriteStartArray("tracks");
                foreach (var entry in playlist.Tracks.OrderBy(t => t.Order))
                {
                    var path = entry.Music?.FilePath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("filePath", path);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (IOException)
        {
            // A stale snapshot is harmless; it is replaced on the next successful save.
        }
    }

    // ---------------------------------------------------------------- writing

    private static void WriteTrack(Utf8JsonWriter writer, Music track)
    {
        writer.WriteStartObject();

        writer.WriteNumber("id", track.Id);
        writer.WriteString("filePath", track.FilePath);
        writer.WriteString("format", track.Format);
        writer.WriteNumber("fileSize", track.FileSize);
        writer.WriteNumber("durationMillis", (long)track.Duration.TotalMilliseconds);

        writer.WriteString("name", track.Name);
        writer.WriteString("albumArtist", track.AlbumArtist);
        WriteNullableInt(writer, "year", track.Year);
        WriteNullableInt(writer, "trackNumber", track.TrackNumber);
        WriteNullableInt(writer, "discNumber", track.DiscNumber);

        writer.WriteString("artistName", track.Artist?.Name ?? string.Empty);
        writer.WriteString("albumTitle", track.Album?.Title ?? string.Empty);
        writer.WriteString("albumAlbumArtist", track.Album?.AlbumArtist ?? string.Empty);
        WriteNullableInt(writer, "albumYear", track.Album?.Year);
        writer.WriteString("albumArtworkPath", track.Album?.ArtworkPath);

        writer.WriteStartArray("genres");
        foreach (var genre in track.Genres)
        {
            writer.WriteStringValue(genre.Name);
        }

        writer.WriteEndArray();

        if (track.Artwork is { } artwork)
        {
            writer.WritePropertyName("artwork");
            writer.WriteStartObject();
            writer.WriteString("filePath", artwork.FilePath);
            writer.WriteString("thumbnailPath", artwork.ThumbnailPath);
            writer.WriteString("mimeType", artwork.MimeType);
            writer.WriteNumber("fileSize", artwork.FileSize);
            writer.WriteString("source", artwork.Source.ToString());
            writer.WriteEndObject();
        }

        if (track.Lyrics is { } lyrics && !string.IsNullOrEmpty(lyrics.Text))
        {
            writer.WritePropertyName("lyrics");
            writer.WriteStartObject();
            writer.WriteString("type", lyrics.Type.ToString());
            writer.WriteString("source", lyrics.Source.ToString());
            writer.WriteString("text", lyrics.Text);
            writer.WriteEndObject();
        }

        if (track.AudioFileInfo is { } audio)
        {
            writer.WritePropertyName("audio");
            writer.WriteStartObject();
            writer.WriteString("codec", audio.Codec);
            writer.WriteNumber("bitrateKbps", audio.BitrateKbps);
            writer.WriteNumber("sampleRateHz", audio.SampleRateHz);
            writer.WriteNumber("channels", audio.Channels);
            writer.WriteBoolean("hasEmbeddedArtwork", audio.HasEmbeddedArtwork);
            writer.WriteEndObject();
        }

        writer.WriteString("dateAddedUtc", track.DateAdded.ToString("O", CultureInfo.InvariantCulture));

        if (track.LastPlayedAt is { } lastPlayed)
        {
            writer.WriteString("lastPlayedAtUtc", lastPlayed.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull("lastPlayedAtUtc");
        }

        writer.WriteEndObject();
    }

    // ---------------------------------------------------------------- reading

    private static Music? ReadTrack(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var track = new Music
        {
            Id = GetInt(element, "id") ?? 0,
            FilePath = GetString(element, "filePath") ?? string.Empty,
            Format = GetString(element, "format") ?? string.Empty,
            FileSize = GetLong(element, "fileSize") ?? 0,
            Duration = TimeSpan.FromMilliseconds(GetLong(element, "durationMillis") ?? 0),
            Name = GetString(element, "name") ?? string.Empty,
            AlbumArtist = GetString(element, "albumArtist") ?? string.Empty,
            Year = GetInt(element, "year"),
            TrackNumber = GetInt(element, "trackNumber"),
            DiscNumber = GetInt(element, "discNumber"),
            DateAdded = GetDate(element, "dateAddedUtc") ?? DateTime.UtcNow,
            LastPlayedAt = GetDate(element, "lastPlayedAtUtc"),
        };

        var artistName = GetString(element, "artistName");
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            track.Artist = new Artist { Name = artistName };
        }

        var albumTitle = GetString(element, "albumTitle");
        if (!string.IsNullOrWhiteSpace(albumTitle))
        {
            track.Album = new Album
            {
                Title = albumTitle,
                AlbumArtist = GetString(element, "albumAlbumArtist") ?? track.AlbumArtist,
                Year = GetInt(element, "albumYear"),
                ArtworkPath = GetString(element, "albumArtworkPath"),
            };
        }

        if (element.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            foreach (var genre in genres.EnumerateArray())
            {
                var name = genre.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    track.Genres.Add(new Genre { Name = name });
                }
            }
        }

        if (element.TryGetProperty("artwork", out var artwork) && artwork.ValueKind == JsonValueKind.Object)
        {
            track.Artwork = new Artwork
            {
                FilePath = GetString(artwork, "filePath") ?? string.Empty,
                ThumbnailPath = GetString(artwork, "thumbnailPath"),
                MimeType = GetString(artwork, "mimeType") ?? "image/jpeg",
                FileSize = GetLong(artwork, "fileSize") ?? 0,
                Source = ParseEnum(GetString(artwork, "source"), ArtworkSource.Embedded),
            };
        }

        if (element.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.Object)
        {
            var text = GetString(lyrics, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                track.Lyrics = new LyricsModel
                {
                    Type = ParseEnum(GetString(lyrics, "type"), LyricsType.Plain),
                    Source = ParseEnum(GetString(lyrics, "source"), LyricsSource.EmbeddedMetadata),
                    Text = text,
                };
            }
        }

        if (element.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object)
        {
            track.AudioFileInfo = new AudioFileInfo
            {
                Codec = GetString(audio, "codec") ?? string.Empty,
                BitrateKbps = GetInt(audio, "bitrateKbps") ?? 0,
                SampleRateHz = GetInt(audio, "sampleRateHz") ?? 0,
                Channels = GetInt(audio, "channels") ?? 0,
                HasEmbeddedArtwork = audio.TryGetProperty("hasEmbeddedArtwork", out var has) && has.ValueKind == JsonValueKind.True,
            };
        }

        return track;
    }

    // ---------------------------------------------------------------- helpers

    private static void WriteNullableInt(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } v)
        {
            writer.WriteNumber(name, v);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : null;

    private static DateTime? GetDate(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, out var parsed) ? parsed : fallback;
}
