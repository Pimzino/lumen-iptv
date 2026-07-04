using System.Net.Http;
using System.Text.Json;

namespace Lumen.Providers.Artwork;

/// <summary>
/// TVMaze — keyless fallback for series artwork when no TMDB credential exists. The search
/// response carries the show poster; a second call fetches a wide "background" image only
/// when the query asks for a backdrop (detail pages).
/// </summary>
public sealed class TvMazeArtworkProvider : IArtworkProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TvMazeArtworkProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "tvmaze";

    public bool CanServe(ArtworkQuery query) => query.IsSeries;

    public async Task<ArtworkResult?> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(TmdbArtworkProvider.HttpClientName);
        var url = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query.Title)}";

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument json;
        await using (stream.ConfigureAwait(false))
        {
            json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        using var _ = json;
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var bestScore = 0;
        long showId = 0;
        string? poster = null;

        foreach (var entry in json.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("show", out var show))
            {
                continue;
            }

            var title = show.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var year = ArtworkMatcher.YearOf(show.TryGetProperty("premiered", out var p) ? p.GetString() : null);
            var score = ArtworkMatcher.Score(title, year, query.Title, query.Year);
            if (score <= bestScore)
            {
                continue;
            }

            var image = show.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object
                && img.TryGetProperty("original", out var orig)
                    ? orig.GetString()
                    : null;
            if (image is null)
            {
                continue;
            }

            bestScore = score;
            poster = image;
            showId = show.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0;
        }

        if (bestScore < ArtworkMatcher.Threshold || poster is null)
        {
            return null;
        }

        string? backdrop = null;
        if (query.WantBackdrop && showId > 0)
        {
            backdrop = await TryGetBackgroundAsync(client, showId, cancellationToken).ConfigureAwait(false);
        }

        return new ArtworkResult(poster, backdrop, Name);
    }

    private static async Task<string?> TryGetBackgroundAsync(HttpClient client, long showId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"https://api.tvmaze.com/shows/{showId}/images", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument json;
            await using (stream.ConfigureAwait(false))
            {
                json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            using var _ = json;
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var image in json.RootElement.EnumerateArray())
            {
                var type = image.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "background")
                {
                    continue;
                }

                if (image.TryGetProperty("resolutions", out var res)
                    && res.TryGetProperty("original", out var original)
                    && original.TryGetProperty("url", out var u))
                {
                    return u.GetString();
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // the poster already succeeded; a missing backdrop is not a failure
        }
    }
}
