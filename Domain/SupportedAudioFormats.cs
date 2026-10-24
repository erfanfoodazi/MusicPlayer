namespace Domain;

/// <summary>
/// The audio file extensions Auralis recognises when scanning (spec section 4).
///
/// NOTE: inclusion here means "we attempt to read metadata from it". It does NOT mean the
/// format can be decoded for playback on every platform - playback capability is a separate
/// per-platform question handled by the playback engine (spec section 4: do not confuse
/// decoding support with tagging support).
/// </summary>
public static class SupportedAudioFormats
{
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".mp3",
        ".flac",
        ".wav",
        ".aac",
        ".m4a",
        ".ogg",
        ".opus",
        ".aif",
        ".aiff",
        ".alac",
        ".ape",
        ".wma",
    ];

    /// <summary>Returns true when the path's extension is one we scan for (case-insensitive).</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;

        foreach (var supported in Extensions)
        {
            if (ext.Equals(supported, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
