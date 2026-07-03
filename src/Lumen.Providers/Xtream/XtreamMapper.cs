using Lumen.Core.Models;

namespace Lumen.Providers.Xtream;

/// <summary>Maps Xtream DTOs onto the provider-neutral domain models.</summary>
public static class XtreamMapper
{
    public static Category ToCategory(XtreamCategory dto, long profileId, ContentKind kind, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new Category
        {
            ProfileId = profileId,
            ProviderCategoryId = dto.CategoryId ?? string.Empty,
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(dto.CategoryName) ? "Unnamed" : dto.CategoryName,
            SortOrder = sortOrder,
        };
    }

    /// <summary>Null when the stream has no usable id or name.</summary>
    public static Channel? ToChannel(
        XtreamLiveStream dto,
        long profileId,
        IReadOnlyDictionary<string, long> categoryIdsByProviderId,
        long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(categoryIdsByProviderId);
        if (string.IsNullOrWhiteSpace(dto.StreamId) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        return new Channel
        {
            ProfileId = profileId,
            CategoryId = ResolveCategory(dto.CategoryId, categoryIdsByProviderId),
            ProviderStreamId = dto.StreamId,
            Number = dto.Number,
            Name = dto.Name,
            LogoUrl = NullIfBlank(dto.StreamIcon),
            EpgChannelId = NullIfBlank(dto.EpgChannelId),
            AddedUtc = nowUnix,
        };
    }

    /// <summary>Null when the movie has no usable id or name.</summary>
    public static VodItem? ToMovie(
        XtreamVodStream dto,
        long profileId,
        IReadOnlyDictionary<string, long> categoryIdsByProviderId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(categoryIdsByProviderId);
        if (string.IsNullOrWhiteSpace(dto.StreamId) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        return new VodItem
        {
            ProfileId = profileId,
            Kind = ContentKind.Movie,
            ProviderItemId = dto.StreamId,
            CategoryId = ResolveCategory(dto.CategoryId, categoryIdsByProviderId),
            Name = dto.Name,
            PosterUrl = NullIfBlank(dto.StreamIcon),
            Rating = dto.Rating,
            ProviderAddedUtc = dto.AddedUnix,
            ContainerExtension = NullIfBlank(dto.ContainerExtension),
        };
    }

    /// <summary>Null when the series has no usable id or name.</summary>
    public static VodItem? ToSeries(
        XtreamSeries dto,
        long profileId,
        IReadOnlyDictionary<string, long> categoryIdsByProviderId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(categoryIdsByProviderId);
        if (string.IsNullOrWhiteSpace(dto.SeriesId) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        return new VodItem
        {
            ProfileId = profileId,
            Kind = ContentKind.Series,
            ProviderItemId = dto.SeriesId,
            CategoryId = ResolveCategory(dto.CategoryId, categoryIdsByProviderId),
            Name = dto.Name,
            PosterUrl = NullIfBlank(dto.Cover),
            Rating = dto.Rating,
            ProviderAddedUtc = dto.LastModifiedUnix,
            Year = YearOf(dto.ReleaseDate),
        };
    }

    public static MovieDetails ToMovieDetails(XtreamVodInfo dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var info = dto.Info;
        return new MovieDetails
        {
            Plot = NullIfBlank(info?.Plot) ?? NullIfBlank(info?.Description),
            Genre = NullIfBlank(info?.Genre),
            Director = NullIfBlank(info?.Director),
            Cast = NullIfBlank(info?.Cast) ?? NullIfBlank(info?.Actors),
            DurationSeconds = info?.DurationSeconds,
            BackdropUrl = info?.BackdropPath?.FirstOrDefault(),
            TrailerUrl = ToTrailerUrl(info?.YoutubeTrailer),
            Rating = info?.Rating,
            ReleaseDate = NullIfBlank(info?.ReleaseDate),
            ContainerExtension = NullIfBlank(dto.MovieData?.ContainerExtension),
        };
    }

    public static SeriesDetails ToSeriesDetails(XtreamSeriesInfo dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var seasons = new List<SeriesSeason>();

        foreach (var pair in (dto.Episodes ?? []).OrderBy(p => ParseSeason(p.Key)))
        {
            var seasonNumber = ParseSeason(pair.Key);
            var episodes = pair.Value
                .Where(e => !string.IsNullOrWhiteSpace(e.Id))
                .Select(e => new SeriesEpisode
                {
                    ProviderEpisodeId = e.Id!,
                    Title = string.IsNullOrWhiteSpace(e.Title)
                        ? $"Episode {e.EpisodeNumber ?? 0}"
                        : e.Title,
                    Season = e.Season ?? seasonNumber,
                    Number = e.EpisodeNumber ?? 0,
                    Plot = NullIfBlank(e.Info?.Plot),
                    DurationSeconds = e.Info?.DurationSeconds,
                    PosterUrl = NullIfBlank(e.Info?.MovieImage),
                    ContainerExtension = NullIfBlank(e.ContainerExtension),
                    Rating = e.Info?.Rating,
                    AirDate = NullIfBlank(e.Info?.ReleaseDate),
                })
                .OrderBy(e => e.Number)
                .ToList();

            if (episodes.Count > 0)
            {
                seasons.Add(new SeriesSeason { Number = seasonNumber, Episodes = episodes });
            }
        }

        return new SeriesDetails
        {
            Plot = NullIfBlank(dto.Info?.Plot),
            Genre = NullIfBlank(dto.Info?.Genre),
            Cast = NullIfBlank(dto.Info?.Cast),
            Director = NullIfBlank(dto.Info?.Director),
            Rating = dto.Info?.Rating,
            ReleaseDate = NullIfBlank(dto.Info?.ReleaseDate),
            BackdropUrl = dto.Info?.BackdropPath?.FirstOrDefault(),
            Seasons = seasons,
        };
    }

    private static long? ResolveCategory(
        string? providerCategoryId, IReadOnlyDictionary<string, long> categoryIdsByProviderId) =>
        providerCategoryId is not null && categoryIdsByProviderId.TryGetValue(providerCategoryId, out var id)
            ? id
            : null;

    private static int ParseSeason(string key) =>
        int.TryParse(key, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var season)
            ? season
            : 0;

    internal static int? YearOf(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        return int.TryParse(releaseDate.AsSpan(0, 4), out var year) && year is >= 1900 and <= 2100
            ? year
            : null;
    }

    private static string? ToTrailerUrl(string? youtubeId) =>
        string.IsNullOrWhiteSpace(youtubeId)
            ? null
            : youtubeId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? youtubeId
                : $"https://www.youtube.com/watch?v={youtubeId}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
