using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lumen.Providers.Xtream.Json;
using Microsoft.Extensions.Logging;

namespace Lumen.Providers.Xtream;

/// <summary>Default <see cref="IXtreamClient"/> over HttpClient.</summary>
public sealed class XtreamClient : IXtreamClient
{
    private readonly HttpClient _http;
    private readonly ILogger<XtreamClient> _logger;

    public XtreamClient(HttpClient http, XtreamCredentials credentials, ILogger<XtreamClient> logger)
    {
        _http = http;
        Credentials = credentials;
        _logger = logger;
    }

    public XtreamCredentials Credentials { get; }

    public async Task<XtreamAuthResponse> AuthenticateAsync(CancellationToken cancellationToken)
    {
        using var document = await GetDocumentAsync(action: null, cancellationToken).ConfigureAwait(false);
        var response = document.RootElement.Deserialize(XtreamJsonContext.Default.XtreamAuthResponse);
        return response ?? throw new XtreamApiException("The server's authentication response was empty.");
    }

    public Task<IReadOnlyList<XtreamCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken) =>
        GetListAsync("get_live_categories", XtreamJsonContext.Default.XtreamCategory, cancellationToken);

    public Task<IReadOnlyList<XtreamCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken) =>
        GetListAsync("get_vod_categories", XtreamJsonContext.Default.XtreamCategory, cancellationToken);

    public Task<IReadOnlyList<XtreamCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken) =>
        GetListAsync("get_series_categories", XtreamJsonContext.Default.XtreamCategory, cancellationToken);

    public Task<IReadOnlyList<XtreamLiveStream>> GetLiveStreamsAsync(string? categoryId, CancellationToken cancellationToken) =>
        GetListAsync("get_live_streams", XtreamJsonContext.Default.XtreamLiveStream, cancellationToken, CategoryFilter(categoryId));

    public Task<IReadOnlyList<XtreamVodStream>> GetVodStreamsAsync(string? categoryId, CancellationToken cancellationToken) =>
        GetListAsync("get_vod_streams", XtreamJsonContext.Default.XtreamVodStream, cancellationToken, CategoryFilter(categoryId));

    public Task<IReadOnlyList<XtreamSeries>> GetSeriesAsync(string? categoryId, CancellationToken cancellationToken) =>
        GetListAsync("get_series", XtreamJsonContext.Default.XtreamSeries, cancellationToken, CategoryFilter(categoryId));

    public async Task<XtreamVodInfo?> GetVodInfoAsync(string vodId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vodId);
        using var document = await GetDocumentAsync("get_vod_info", cancellationToken, ("vod_id", vodId)).ConfigureAwait(false);
        return SafeDeserialize(document.RootElement, XtreamJsonContext.Default.XtreamVodInfo, "get_vod_info");
    }

    public async Task<XtreamSeriesInfo?> GetSeriesInfoAsync(string seriesId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        using var document = await GetDocumentAsync("get_series_info", cancellationToken, ("series_id", seriesId)).ConfigureAwait(false);
        return SafeDeserialize(document.RootElement, XtreamJsonContext.Default.XtreamSeriesInfo, "get_series_info");
    }

    public async Task<IReadOnlyList<XtreamEpgListing>> GetShortEpgAsync(string streamId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using var document = await GetDocumentAsync(
            "get_short_epg", cancellationToken,
            ("stream_id", streamId), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        var response = SafeDeserialize(document.RootElement, XtreamJsonContext.Default.XtreamShortEpgResponse, "get_short_epg");
        return response?.EpgListings ?? [];
    }

    public async Task<IReadOnlyList<XtreamEpgListing>> GetSimpleDataTableAsync(string streamId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using var document = await GetDocumentAsync(
            "get_simple_data_table", cancellationToken, ("stream_id", streamId)).ConfigureAwait(false);
        var response = SafeDeserialize(document.RootElement, XtreamJsonContext.Default.XtreamShortEpgResponse, "get_simple_data_table");
        return response?.EpgListings ?? [];
    }

    private static (string Key, string Value)[] CategoryFilter(string? categoryId) =>
        string.IsNullOrEmpty(categoryId) ? [] : [("category_id", categoryId)];

    private async Task<IReadOnlyList<T>> GetListAsync<T>(
        string action,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken,
        params (string Key, string Value)[] query)
    {
        using var document = await GetDocumentAsync(action, cancellationToken, query).ConfigureAwait(false);
        var root = document.RootElement;

        // Empty catalogs come back as {} or false on some panels.
        if (root.ValueKind != JsonValueKind.Array)
        {
            _logger.LogDebug("{Action} returned {Kind} instead of an array; treating as empty", action, root.ValueKind);
            return [];
        }

        var items = new List<T>(root.GetArrayLength());
        var skipped = 0;
        foreach (var element in root.EnumerateArray())
        {
            try
            {
                var item = element.Deserialize(typeInfo);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (JsonException ex)
            {
                skipped++;
                _logger.LogDebug(ex, "Skipped malformed {Action} item", action);
            }
        }

        if (skipped > 0)
        {
            _logger.LogWarning("{Action}: skipped {Skipped} malformed item(s) of {Total}", action, skipped, root.GetArrayLength());
        }

        return items;
    }

    private T? SafeDeserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo, string action)
        where T : class
    {
        try
        {
            return element.Deserialize(typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Action}: response could not be parsed", action);
            return null;
        }
    }

    private async Task<JsonDocument> GetDocumentAsync(
        string? action, CancellationToken cancellationToken, params (string Key, string Value)[] query)
    {
        var uri = XtreamUrls.PlayerApi(Credentials.Server, Credentials.Username, Credentials.Password, action);
        if (query.Length > 0)
        {
            var extra = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
            uri = new Uri($"{uri.AbsoluteUri}&{extra}");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new XtreamApiException($"Could not reach the server: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XtreamApiException("The server did not respond within 15 seconds.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new XtreamApiException(
                    $"The server rejected the request ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Panels behind login portals or CDNs return HTML error pages with status 200.
            var firstByte = FirstMeaningfulByte(payload);
            if (firstByte == '<')
            {
                throw new XtreamApiException(
                    "The server returned a web page instead of data — check the server URL and credentials.");
            }

            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new XtreamApiException("The server sent a response that is not valid JSON.", ex);
            }
        }
    }

    private static byte FirstMeaningfulByte(byte[] payload)
    {
        foreach (var b in payload)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF))
            {
                return b;
            }
        }

        return 0;
    }
}

/// <summary>Default factory creating clients on the "xtream" named HttpClient.</summary>
public sealed class XtreamClientFactory : IXtreamClientFactory
{
    /// <summary>Named HttpClient used for Xtream API calls.</summary>
    public const string HttpClientName = "xtream";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public XtreamClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IXtreamClient Create(XtreamCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new XtreamClient(
            _httpClientFactory.CreateClient(HttpClientName),
            credentials,
            _loggerFactory.CreateLogger<XtreamClient>());
    }
}
