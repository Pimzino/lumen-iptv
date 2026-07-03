namespace Lumen.Providers.Xtream;

/// <summary>Credentials for one Xtream portal account.</summary>
public sealed record XtreamCredentials(string Server, string Username, string Password);

/// <summary>
/// Typed client over an Xtream Codes player_api.php endpoint. Implementations are
/// defensive: malformed list items are skipped and logged rather than failing the call.
/// </summary>
public interface IXtreamClient
{
    XtreamCredentials Credentials { get; }

    /// <summary>Authenticates and returns account + server info.</summary>
    Task<XtreamAuthResponse> AuthenticateAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamLiveStream>> GetLiveStreamsAsync(string? categoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamVodStream>> GetVodStreamsAsync(string? categoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamSeries>> GetSeriesAsync(string? categoryId, CancellationToken cancellationToken);

    Task<XtreamVodInfo?> GetVodInfoAsync(string vodId, CancellationToken cancellationToken);

    Task<XtreamSeriesInfo?> GetSeriesInfoAsync(string seriesId, CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamEpgListing>> GetShortEpgAsync(string streamId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<XtreamEpgListing>> GetSimpleDataTableAsync(string streamId, CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="IXtreamClient"/> instances bound to a profile's credentials.</summary>
public interface IXtreamClientFactory
{
    IXtreamClient Create(XtreamCredentials credentials);
}
