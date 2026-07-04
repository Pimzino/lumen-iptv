using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Lumen.Providers.Artwork;

/// <summary>
/// The Movie Database (themoviedb.org) — the highest-quality source, used whenever the user
/// has supplied a TMDB credential (either a v3 API key or a v4 read access token; both are
/// accepted and detected by shape). Serves movies and series, posters and backdrops.
/// </summary>
public sealed class TmdbArtworkProvider : IArtworkProvider
{
    /// <summary>Named HttpClient this provider resolves.</summary>
    public const string HttpClientName = "artwork";

    private const string PosterBase = "https://image.tmdb.org/t/p/w500";
    private const string BackdropBase = "https://image.tmdb.org/t/p/w1280";

    private readonly IHttpClientFactory _httpClientFactory;

    public TmdbArtworkProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "tmdb";

    public bool CanServe(ArtworkQuery query) => !string.IsNullOrWhiteSpace(query.TmdbApiKey);

    public async Task<ArtworkResult?> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        var result = await SearchAsync(query, query.Year, cancellationToken).ConfigureAwait(false);

        // A year hint narrows the search but can also miss (provider year off by one, or the
        // "year" was really part of the title) — retry unfiltered before giving up.
        if (result is null && query.Year is not null)
        {
            result = await SearchAsync(query, year: null, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ArtworkResult?> SearchAsync(ArtworkQuery query, int? year, CancellationToken cancellationToken)
    {
        var key = query.TmdbApiKey!.Trim();
        var isBearer = key.StartsWith("ey", StringComparison.Ordinal) && key.Length > 60;

        var path = query.IsSeries ? "search/tv" : "search/movie";
        var yearParam = year is null
            ? string.Empty
            : query.IsSeries ? $"&first_air_date_year={year}" : $"&primary_release_year={year}";
        var keyParam = isBearer ? string.Empty : $"&api_key={Uri.EscapeDataString(key)}";
        var url = $"https://api.themoviedb.org/3/{path}?query={Uri.EscapeDataString(query.Title)}&include_adult=false{yearParam}{keyParam}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (isBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

        var titleField = query.IsSeries ? "name" : "title";
        var dateField = query.IsSeries ? "first_air_date" : "release_date";

        var bestScore = 0;
        string? poster = null;
        string? backdrop = null;

        foreach (var item in results.EnumerateArray().Take(10))
        {
            var title = item.TryGetProperty(titleField, out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var candidateYear = ArtworkMatcher.YearOf(item.TryGetProperty(dateField, out var d) ? d.GetString() : null);
            var score = ArtworkMatcher.Score(title, candidateYear, query.Title, query.Year);
            if (score <= bestScore)
            {
                continue;
            }

            var posterPath = item.TryGetProperty("poster_path", out var p) ? p.GetString() : null;
            var backdropPath = item.TryGetProperty("backdrop_path", out var b) ? b.GetString() : null;
            if (posterPath is null && backdropPath is null)
            {
                continue;
            }

            bestScore = score;
            poster = posterPath is null ? null : PosterBase + posterPath;
            backdrop = backdropPath is null ? null : BackdropBase + backdropPath;
        }

        return bestScore >= ArtworkMatcher.Threshold
            ? new ArtworkResult(poster, backdrop, Name)
            : null;
    }
}
