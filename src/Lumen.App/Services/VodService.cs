using Lumen.Core.Models;
using Lumen.Providers.Xtream;

namespace Lumen.App.Services;

/// <summary>Resolves VOD detail metadata and playable stream URLs across providers.</summary>
public sealed class VodService
{
    private readonly IXtreamClientFactory _xtreamFactory;
    private readonly ISessionService _session;

    public VodService(IXtreamClientFactory xtreamFactory, ISessionService session)
    {
        _xtreamFactory = xtreamFactory;
        _session = session;
    }

    /// <summary>Fetches extended movie metadata. Null for M3U items (no detail endpoint).</summary>
    public async Task<MovieDetails?> GetMovieDetailsAsync(VodItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var profile = _session.CurrentProfile;
        if (profile is null || profile.Kind != ProfileKind.Xtream ||
            _session.GetXtreamCredentials(profile) is not { })
        {
            return null;
        }

        var client = _xtreamFactory.Create(_session.GetXtreamCredentials(profile)!);
        var info = await client.GetVodInfoAsync(item.ProviderItemId, cancellationToken);
        return info is null ? null : XtreamMapper.ToMovieDetails(info);
    }

    /// <summary>Fetches a series' seasons and episodes. Null for M3U items.</summary>
    public async Task<SeriesDetails?> GetSeriesDetailsAsync(VodItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var profile = _session.CurrentProfile;
        if (profile is null || profile.Kind != ProfileKind.Xtream ||
            _session.GetXtreamCredentials(profile) is not { })
        {
            return null;
        }

        var client = _xtreamFactory.Create(_session.GetXtreamCredentials(profile)!);
        var info = await client.GetSeriesInfoAsync(item.ProviderItemId, cancellationToken);
        return info is null ? null : XtreamMapper.ToSeriesDetails(info);
    }

    /// <summary>Builds the stream URL for a movie under the active profile. Null when unresolved.</summary>
    public string? ResolveMovieUrl(VodItem item, string? containerExtension) =>
        ResolveMovieUrl(_session.CurrentProfile, item, containerExtension);

    /// <summary>
    /// Builds the stream URL for a movie under a specific profile — used by the download engine,
    /// which resolves a queued job against its <b>stored</b> profile rather than the active one.
    /// </summary>
    public string? ResolveMovieUrl(Profile? profile, VodItem item, string? containerExtension)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ResolveMovieUrl(
            profile, item.ProviderItemId, containerExtension ?? item.ContainerExtension, item.StreamUrl);
    }

    /// <summary>
    /// Core movie URL builder (single source of truth): an M3U-direct URL wins; otherwise the
    /// profile's Xtream credentials build the endpoint. Credentials are decrypted on demand and
    /// never persisted in the resolved URL.
    /// </summary>
    public string? ResolveMovieUrl(
        Profile? profile, string providerItemId, string? containerExtension, string? m3uStreamUrl)
    {
        if (!string.IsNullOrWhiteSpace(m3uStreamUrl))
        {
            return m3uStreamUrl;
        }

        if (profile is null || _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        return XtreamUrls.Movie(
            credentials.Server, credentials.Username, credentials.Password,
            providerItemId, containerExtension).AbsoluteUri;
    }

    /// <summary>Builds the stream URL for a series episode under the active profile. Null when unresolved.</summary>
    public string? ResolveEpisodeUrl(SeriesEpisode episode) =>
        ResolveEpisodeUrl(_session.CurrentProfile, episode);

    /// <summary>Builds a series-episode URL under a specific profile (download engine path).</summary>
    public string? ResolveEpisodeUrl(Profile? profile, SeriesEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return ResolveEpisodeUrl(profile, episode.ProviderEpisodeId, episode.ContainerExtension);
    }

    /// <summary>Core series-episode URL builder (single source of truth).</summary>
    public string? ResolveEpisodeUrl(Profile? profile, string providerEpisodeId, string? containerExtension)
    {
        if (profile is null || _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        return XtreamUrls.SeriesEpisode(
            credentials.Server, credentials.Username, credentials.Password,
            providerEpisodeId, containerExtension).AbsoluteUri;
    }
}
