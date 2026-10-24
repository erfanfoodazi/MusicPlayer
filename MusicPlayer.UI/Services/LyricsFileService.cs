namespace MusicPlayer.UI.Services;

/// <summary>
/// Importing and exporting .lrc files. The base implementation works on Android: importing
/// uses the system file picker; exporting writes the file into app storage and opens the
/// share sheet so the user can save it anywhere. Windows overrides exporting with the native
/// save dialog (see <c>WindowsLyricsFileService</c>).
/// </summary>
public class LyricsFileService
{
    /// <summary>Opens the file picker and returns the text of the chosen .lrc, or null.</summary>
    public async Task<string?> ImportLrcTextAsync()
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Choose a lyrics file (.lrc)",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    // .lrc has no official MIME type, so Android offers text plus a catch-all.
                    { DevicePlatform.Android, new[] { "text/plain", "application/octet-stream", "*/*" } },
                    { DevicePlatform.WinUI, new[] { ".lrc", ".txt" } },
                }),
            };

            var result = await FilePicker.Default.PickAsync(options);
            if (result is null)
            {
                return null;
            }

            using var stream = await result.OpenReadAsync();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Offers the given lyrics text as a .lrc file. Returns true when the user saved/shared
    /// it; false when the action was cancelled or failed.
    /// </summary>
    public virtual async Task<bool> ExportLrcAsync(string suggestedFileName, string content)
    {
        try
        {
            var exportDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "exports");
            Directory.CreateDirectory(exportDirectory);

            var safeName = SanitizeFileName(suggestedFileName);
            var path = Path.Combine(exportDirectory, safeName);
            File.WriteAllText(path, content);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = safeName,
                File = new ShareFile(path),
            });

            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Builds a safe, readable file name for an exported .lrc.</summary>
    public static string SuggestFileName(string? title, string? artist)
    {
        var name = string.IsNullOrWhiteSpace(artist)
            ? (title ?? string.Empty)
            : $"{title} - {artist}";

        var sanitized = SanitizeFileName(name.Trim());
        // SanitizeFileName falls back to "lyrics.lrc" for empty input; keep one extension.
        return sanitized.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : sanitized + ".lrc";
    }

    protected static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "lyrics.lrc" : result;
    }
}
