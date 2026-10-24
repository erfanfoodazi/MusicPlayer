namespace MusicPlayer.UI.Services;

/// <summary>
/// Finds the audio files that make up the library. Android uses the system media index so
/// music is found in Download, Telegram, WhatsApp and other folders as well as Music.
/// Desktop platforms keep using the user-selected folder.
/// </summary>
public interface IMusicFileDiscovery
{
    /// <summary>True when <see cref="DiscoverAsync"/> returns the whole device library.</summary>
    bool ScansWholeLibrary { get; }

    /// <summary>Path stored in the library snapshot for the whole-library source.</summary>
    string SourcePath { get; }

    /// <summary>Short, user-facing name for the source, used in scan status text.</summary>
    string SourceLabel { get; }

    Task<IReadOnlyList<string>> DiscoverAsync(string? preferredFolder, CancellationToken cancellationToken = default);
}
