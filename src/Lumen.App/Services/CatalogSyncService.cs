using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Lumen.Providers;
using Lumen.Providers.M3u;
using Lumen.Providers.Xtream;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>Outcome of a catalog sync.</summary>
public sealed record CatalogSyncResult(int Channels, int Movies, int Series);

/// <summary>Imports a profile's channels and VOD catalog from its provider.</summary>
public interface ICatalogSyncService
{
    Task<CatalogSyncResult> SyncAsync(Profile profile, CancellationToken cancellationToken);
}

/// <summary>
/// Provider-dispatching sync: Xtream profiles pull categories + streams from player_api;
/// M3U profiles stream-parse the playlist and classify entries into live/movie/series,
/// honoring per-group kind overrides the user made.
/// </summary>
public sealed class CatalogSyncService : ICatalogSyncService
{
    private readonly IXtreamClientFactory _xtreamFactory;
    private readonly IM3uPlaylistParser _m3uParser;
    private readonly ICatalogRepository _catalog;
    private readonly ISessionService _session;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;
    private readonly ILogger<CatalogSyncService> _logger;

    public CatalogSyncService(
        IXtreamClientFactory xtreamFactory,
        IM3uPlaylistParser m3uParser,
        ICatalogRepository catalog,
        ISessionService session,
        IHttpClientFactory httpClientFactory,
        IClock clock,
        IMessenger messenger,
        ILogger<CatalogSyncService> logger)
    {
        _xtreamFactory = xtreamFactory;
        _m3uParser = m3uParser;
        _catalog = catalog;
        _session = session;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
        _messenger = messenger;
        _logger = logger;
    }

    public async Task<CatalogSyncResult> SyncAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = profile.Kind == ProfileKind.Xtream
            ? await SyncXtreamAsync(profile, cancellationToken)
            : await SyncM3uAsync(profile, cancellationToken);

        _logger.LogInformation(
            "Catalog sync for {Profile}: {Channels} channels, {Movies} movies, {Series} series",
            profile.Name, result.Channels, result.Movies, result.Series);
        _messenger.Send(new CatalogRefreshedMessage(profile.Id));
        return result;
    }

    private async Task<CatalogSyncResult> SyncXtreamAsync(Profile profile, CancellationToken cancellationToken)
    {
        var credentials = _session.GetXtreamCredentials(profile)
            ?? throw new InvalidOperationException("Profile has no stored Xtream credentials.");
        var client = _xtreamFactory.Create(credentials);
        var now = _clock.UtcNow.ToUnixTimeSeconds();

        // Live
        var liveCategories = await client.GetLiveCategoriesAsync(cancellationToken);
        var liveMap = await ReplaceCategoriesAsync(profile.Id, ContentKind.Live, liveCategories, cancellationToken);
        var liveStreams = await client.GetLiveStreamsAsync(null, cancellationToken);
        var channels = liveStreams
            .Select(s => XtreamMapper.ToChannel(s, profile.Id, liveMap, now))
            .OfType<Channel>()
            .ToList();
        await _catalog.UpsertChannelsAsync(profile.Id, channels, cancellationToken);

        // Movies
        var vodCategories = await client.GetVodCategoriesAsync(cancellationToken);
        var vodMap = await ReplaceCategoriesAsync(profile.Id, ContentKind.Movie, vodCategories, cancellationToken);
        var vodStreams = await client.GetVodStreamsAsync(null, cancellationToken);
        var movies = vodStreams
            .Select(s => XtreamMapper.ToMovie(s, profile.Id, vodMap))
            .OfType<VodItem>()
            .ToList();
        await _catalog.UpsertVodItemsAsync(profile.Id, ContentKind.Movie, movies, cancellationToken);

        // Series
        var seriesCategories = await client.GetSeriesCategoriesAsync(cancellationToken);
        var seriesMap = await ReplaceCategoriesAsync(profile.Id, ContentKind.Series, seriesCategories, cancellationToken);
        var seriesList = await client.GetSeriesAsync(null, cancellationToken);
        var series = seriesList
            .Select(s => XtreamMapper.ToSeries(s, profile.Id, seriesMap))
            .OfType<VodItem>()
            .ToList();
        await _catalog.UpsertVodItemsAsync(profile.Id, ContentKind.Series, series, cancellationToken);

        return new CatalogSyncResult(channels.Count, movies.Count, series.Count);
    }

    private async Task<Dictionary<string, long>> ReplaceCategoriesAsync(
        long profileId,
        ContentKind kind,
        IReadOnlyList<XtreamCategory> dtos,
        CancellationToken cancellationToken)
    {
        var categories = dtos
            .Where(c => !string.IsNullOrWhiteSpace(c.CategoryId))
            .Select((c, index) => XtreamMapper.ToCategory(c, profileId, kind, index))
            .ToList();
        await _catalog.ReplaceCategoriesAsync(profileId, kind, categories, cancellationToken);
        return categories.ToDictionary(c => c.ProviderCategoryId, c => c.Id, StringComparer.Ordinal);
    }

    private async Task<CatalogSyncResult> SyncM3uAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.PlaylistSource))
        {
            throw new InvalidOperationException("Profile has no playlist source.");
        }

        // Existing user overrides re-classify whole groups.
        var overrides = new Dictionary<string, ContentKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { ContentKind.Live, ContentKind.Movie, ContentKind.Series })
        {
            foreach (var category in await _catalog.GetCategoriesAsync(profile.Id, kind, cancellationToken))
            {
                if (category.ContentKindOverride is { } forced)
                {
                    overrides[category.ProviderCategoryId] = forced;
                }
            }
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var channels = new List<(Channel Channel, string Group)>();
        var movies = new List<(VodItem Item, string Group)>();
        var series = new List<(VodItem Item, string Group)>();
        var groupKinds = new Dictionary<string, ContentKind>(StringComparer.OrdinalIgnoreCase);

        var stream = await OpenPlaylistAsync(profile, cancellationToken);
        await using (stream)
        {
            await foreach (var entry in _m3uParser.ParseAsync(stream, cancellationToken))
            {
                var group = string.IsNullOrWhiteSpace(entry.GroupTitle) ? "Uncategorized" : entry.GroupTitle;
                var kind = overrides.TryGetValue(group, out var forced)
                    ? forced
                    : M3uContentClassifier.Classify(entry);
                groupKinds.TryAdd(group, kind);

                switch (kind)
                {
                    case ContentKind.Movie:
                        movies.Add((ToVodItem(entry, profile.Id, ContentKind.Movie, now), group));
                        break;
                    case ContentKind.Series:
                        series.Add((ToVodItem(entry, profile.Id, ContentKind.Series, now), group));
                        break;
                    default:
                        channels.Add((new Channel
                        {
                            ProfileId = profile.Id,
                            Name = entry.Title,
                            StreamUrl = entry.Url,
                            LogoUrl = entry.LogoUrl,
                            EpgChannelId = entry.TvgId,
                            TvgShiftMinutes = entry.TvgShiftMinutes,
                            UserAgent = entry.UserAgent,
                            Referrer = entry.Referrer,
                            AddedUtc = now,
                        }, group));
                        break;
                }
            }
        }

        // Persist categories per kind, then link items to their category ids.
        var categoryIds = new Dictionary<(ContentKind Kind, string Group), long>();
        foreach (var kind in new[] { ContentKind.Live, ContentKind.Movie, ContentKind.Series })
        {
            var groups = groupKinds
                .Where(g => g.Value == kind)
                .Select(g => g.Key)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var categories = groups
                .Select((g, index) => new Category
                {
                    ProfileId = profile.Id,
                    ProviderCategoryId = g,
                    Kind = kind,
                    Name = g,
                    SortOrder = index,
                })
                .ToList();
            await _catalog.ReplaceCategoriesAsync(profile.Id, kind, categories, cancellationToken);
            foreach (var category in categories)
            {
                categoryIds[(kind, category.ProviderCategoryId)] = category.Id;
            }
        }

        foreach (var (channel, group) in channels)
        {
            if (categoryIds.TryGetValue((ContentKind.Live, group), out var id))
            {
                channel.CategoryId = id;
            }
        }

        foreach (var (item, group) in movies)
        {
            if (categoryIds.TryGetValue((ContentKind.Movie, group), out var id))
            {
                item.CategoryId = id;
            }
        }

        foreach (var (item, group) in series)
        {
            if (categoryIds.TryGetValue((ContentKind.Series, group), out var id))
            {
                item.CategoryId = id;
            }
        }

        await _catalog.UpsertChannelsAsync(
            profile.Id, channels.Select(c => c.Channel).ToList(), cancellationToken);
        await _catalog.UpsertVodItemsAsync(
            profile.Id, ContentKind.Movie, movies.Select(m => m.Item).ToList(), cancellationToken);
        await _catalog.UpsertVodItemsAsync(
            profile.Id, ContentKind.Series, series.Select(s => s.Item).ToList(), cancellationToken);

        return new CatalogSyncResult(channels.Count, movies.Count, series.Count);
    }

    private static VodItem ToVodItem(M3uEntry entry, long profileId, ContentKind kind, long now) => new()
    {
        ProfileId = profileId,
        Kind = kind,
        ProviderItemId = entry.Url,
        Name = entry.Title,
        PosterUrl = entry.LogoUrl,
        StreamUrl = entry.Url,
        ProviderAddedUtc = now,
    };

    private async Task<Stream> OpenPlaylistAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.PlaylistIsFile)
        {
            return File.OpenRead(profile.PlaylistSource!);
        }

        var client = _httpClientFactory.CreateClient(ProvidersServiceCollectionExtensions.DownloadHttpClientName);
        var response = await client.GetAsync(
            new Uri(profile.PlaylistSource!), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}
