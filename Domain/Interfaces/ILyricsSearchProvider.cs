using Domain.Models;

namespace Domain.Interfaces;

/// <summary>
/// Port for an online lyrics service. Infrastructure implements it (LRCLIB); the
/// application layer only depends on this interface so tests and future providers
/// (LRCLIB alternatives) can be swapped without touching the use-case code.
/// </summary>
public interface ILyricsSearchProvider
{
    /// <summary>
    /// Searches for lyrics matching the track. Returns preview candidates only; the caller
    /// decides whether to apply one (spec section 8: preview and confirm before applying).
    /// </summary>
    Task<IReadOnlyList<LyricsSearchResult>> SearchAsync(
        Music track,
        CancellationToken cancellationToken = default);
}
