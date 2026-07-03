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

    /// <summary>Builds the stream URL for a movie. Null when it can't be resolved.</summary>
    public string? ResolveMovieUrl(VodItem item, string? containerExtension)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.IsNullOrWhiteSpace(item.StreamUrl))
        {
            return item.StreamUrl;
        }

        var profile = _session.CurrentProfile;
        if (profile is null || _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        return XtreamUrls.Movie(
            credentials.Server, credentials.Username, credentials.Password,
            item.ProviderItemId, containerExtension ?? item.ContainerExtension).AbsoluteUri;
    }

    /// <summary>Builds the stream URL for a series episode. Null when it can't be resolved.</summary>
    public string? ResolveEpisodeUrl(SeriesEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var profile = _session.CurrentProfile;
        if (profile is null || _session.GetXtreamCredentials(profile) is not { } credentials)
        {
            return null;
        }

        return XtreamUrls.SeriesEpisode(
            credentials.Server, credentials.Username, credentials.Password,
            episode.ProviderEpisodeId, episode.ContainerExtension).AbsoluteUri;
    }
}
