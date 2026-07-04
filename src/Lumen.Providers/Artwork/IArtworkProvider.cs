namespace Lumen.Providers.Artwork;

/// <summary>A metadata lookup request for a movie or series title.</summary>
/// <param name="IsSeries">True for series/TV, false for movies.</param>
/// <param name="Title">Cleaned, human-readable title (see <c>TitleCleaner</c>).</param>
/// <param name="Year">Release year hint when the catalog name carried one.</param>
/// <param name="WantBackdrop">True when the caller also wants a wide backdrop (detail pages).</param>
/// <param name="TmdbApiKey">User's TMDB credential; empty disables the TMDB provider.</param>
public sealed record ArtworkQuery(
    bool IsSeries,
    string Title,
    int? Year,
    bool WantBackdrop,
    string? TmdbApiKey = null);

/// <summary>Artwork URLs resolved by a provider. Either url may be null.</summary>
public sealed record ArtworkResult(string? PosterUrl, string? BackdropUrl, string Provider);

/// <summary>
/// An external artwork/metadata source. Implementations are stateless over HttpClient and
/// return null (rather than throw) for "no confident match"; network faults propagate and
/// are handled by the orchestrating service.
/// </summary>
public interface IArtworkProvider
{
    string Name { get; }

    /// <summary>Whether this provider can answer this query at all (kind, credentials).</summary>
    bool CanServe(ArtworkQuery query);

    Task<ArtworkResult?> FindAsync(ArtworkQuery query, CancellationToken cancellationToken);
}
