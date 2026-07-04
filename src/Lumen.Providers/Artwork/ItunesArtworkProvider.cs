using System.Net.Http;
using System.Text.Json;

namespace Lumen.Providers.Artwork;

/// <summary>
/// iTunes Search API — keyless fallback for movie posters when no TMDB credential exists.
/// The catalog's 100×100 thumbnail URL is rewritten to the 600×900 rendition Apple serves
/// from the same asset. No backdrops. Rate limits are modest (~20/min), which the artwork
/// service's cache and low concurrency respect.
/// </summary>
public sealed class ItunesArtworkProvider : IArtworkProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ItunesArtworkProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "itunes";

    public bool CanServe(ArtworkQuery query) => !query.IsSeries;

    public async Task<ArtworkResult?> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query.Title)}&media=movie&entity=movie&limit=10";

        var client = _httpClientFactory.CreateClient(TmdbArtworkProvider.HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument json;
        await using (stream.ConfigureAwait(false))
        {
            json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        using var _ = json;
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var bestScore = 0;
        string? poster = null;

        foreach (var item in results.EnumerateArray())
        {
            var title = item.TryGetProperty("trackName", out var t) ? t.GetString() : null;
            var artwork = item.TryGetProperty("artworkUrl100", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artwork))
            {
                continue;
            }

            var year = ArtworkMatcher.YearOf(item.TryGetProperty("releaseDate", out var d) ? d.GetString() : null);
            var score = ArtworkMatcher.Score(title, year, query.Title, query.Year);
            if (score > bestScore)
            {
                bestScore = score;
                poster = artwork.Replace("100x100bb", "600x900bb", StringComparison.Ordinal);
            }
        }

        return bestScore >= ArtworkMatcher.Threshold && poster is not null
            ? new ArtworkResult(poster, BackdropUrl: null, Name)
            : null;
    }
}
