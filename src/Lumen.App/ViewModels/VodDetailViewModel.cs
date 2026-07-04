using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>An episode card on a series detail page.</summary>
public sealed partial class EpisodeRow : ObservableObject
{
    public required SeriesEpisode Episode { get; init; }

    public string Title => $"{Episode.Number}. {Episode.Title}";

    /// <summary>Two-digit episode number, the monogram shown when there is no thumbnail.</summary>
    public string NumberLabel => Episode.Number.ToString("00", CultureInfo.CurrentCulture);

    public string? ThumbUrl => Episode.PosterUrl;

    public string? Plot => Episode.Plot;

    /// <summary>"42m · 12 Mar 2023 · ★ 8.2" — whichever parts the provider supplied.</summary>
    public string? MetaLine
    {
        get
        {
            var parts = new List<string>(3);
            if (Episode.DurationSeconds is { } secs and > 0)
            {
                var duration = TimeSpan.FromSeconds(secs);
                parts.Add(duration.TotalHours >= 1
                    ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : $"{(int)duration.TotalMinutes}m");
            }

            if (!string.IsNullOrWhiteSpace(Episode.AirDate))
            {
                parts.Add(DateTime.TryParse(
                    Episode.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired)
                    ? aired.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                    : Episode.AirDate);
            }

            if (Episode.Rating is { } rating and > 0)
            {
                parts.Add($"★ {rating:0.0}");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResume))]
    private double _resumeProgress;

    /// <summary>True once a resume position exists, so unwatched cards show no progress track.</summary>
    public bool HasResume => ResumeProgress > 0;
}

/// <summary>A season tab on a series detail page.</summary>
public sealed class SeasonGroup
{
    public required int Number { get; init; }

    public required IReadOnlyList<EpisodeRow> Episodes { get; init; }

    /// <summary>Tab caption; season 0 is the conventional Xtream bucket for specials.</summary>
    public string Label => Number == 0
        ? Strings.Vod_Specials
        : Strings.Format(Strings.Vod_SeasonFormat, Number);

    public string EpisodeCountLabel => Episodes.Count == 1
        ? Strings.Vod_OneEpisode
        : Strings.Format(Strings.Vod_EpisodesCountFormat, Episodes.Count);
}

/// <summary>
/// Movie or series detail page: backdrop, poster, plot, metadata chips, resume support, and
/// (for series) season tabs over an episode-card list. Series get a smart primary action that
/// resumes the most recently watched episode or advances to the next one.
/// </summary>
public sealed partial class VodDetailViewModel : ObservableObject, INavigationAware
{
    /// <summary>Progress beyond which an episode counts as watched and the next one is queued.</summary>
    private const double CompletedProgress = 0.95;

    private readonly VodService _vodService;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IFavoritesRepository _favorites;
    private readonly ISessionService _session;
    private readonly PlaybackService _playback;
    private readonly INavigationService _navigation;
    private readonly ArtworkService _artwork;

    private VodItem? _item;
    private MovieDetails? _movieDetails;
    private WatchHistoryEntry? _resumeEntry;
    private EpisodeRow? _seriesNextUp;
    private bool _hasProviderBackdrop;

    public VodDetailViewModel(
        VodService vodService,
        IWatchHistoryRepository watchHistory,
        IFavoritesRepository favorites,
        ISessionService session,
        PlaybackService playback,
        INavigationService navigation,
        ArtworkService artwork)
    {
        _vodService = vodService;
        _watchHistory = watchHistory;
        _favorites = favorites;
        _session = session;
        _playback = playback;
        _navigation = navigation;
        _artwork = artwork;
    }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isSeries;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _posterUrl;

    [ObservableProperty]
    private string? _backdropUrl;

    [ObservableProperty]
    private string? _plot;

    [ObservableProperty]
    private string? _cast;

    [ObservableProperty]
    private string? _director;

    [ObservableProperty]
    private ObservableCollection<string> _metadataChips = [];

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private string? _resumeLabel;

    /// <summary>Season the episode list is showing; bound to the tab strip.</summary>
    [ObservableProperty]
    private SeasonGroup? _selectedSeason;

    /// <summary>Caption for the series hero button: "Play", "Resume S2 E4", or "Play S3 E1".</summary>
    [ObservableProperty]
    private string? _seriesPlayLabel;

    [ObservableProperty]
    private bool _hasEpisodes;

    [ObservableProperty]
    private bool _showEmptyEpisodes;

    public ObservableCollection<SeasonGroup> Seasons { get; } = [];

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not VodItem item)
        {
            return;
        }

        _item = item;
        _seriesNextUp = null;
        Title = item.Name;
        PosterUrl = item.PosterUrl;
        IsSeries = item.Kind == ContentKind.Series;
        IsLoading = true;
        HasEpisodes = false;
        ShowEmptyEpisodes = false;
        SelectedSeason = null;
        Seasons.Clear();
        MetadataChips = [];

        var profile = _session.CurrentProfile;
        if (profile is not null)
        {
            var favorites = await _favorites.GetAllAsync(profile.Id, cancellationToken);
            IsFavorite = favorites.Any(f => f.ItemKind == item.Kind && f.ItemKey == item.ProviderItemId);
        }

        try
        {
            if (IsSeries)
            {
                await LoadSeriesAsync(item, cancellationToken);
            }
            else
            {
                await LoadMovieAsync(item, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading VOD detail failed");
        }

        IsLoading = false;
        if (IsSeries)
        {
            HasEpisodes = Seasons.Count > 0;
            ShowEmptyEpisodes = !HasEpisodes;
        }

        // Fill missing art from the external metadata services after the page is interactive.
        _ = EnrichArtworkAsync(item, cancellationToken);
    }

    private async Task EnrichArtworkAsync(VodItem item, CancellationToken cancellationToken)
    {
        if (PosterUrl is not null && _hasProviderBackdrop)
        {
            return;
        }

        try
        {
            var art = await _artwork.GetArtworkAsync(item.Kind, item.Name, item.Year, cancellationToken);
            if (art is null || !ReferenceEquals(_item, item))
            {
                return;
            }

            if (PosterUrl is null && art.PosterUrl is not null)
            {
                PosterUrl = art.PosterUrl;
            }

            if (!_hasProviderBackdrop && art.BackdropUrl is not null)
            {
                BackdropUrl = art.BackdropUrl;
            }
        }
        catch (OperationCanceledException)
        {
            // navigated away
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Detail artwork enrichment failed");
        }
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadMovieAsync(VodItem item, CancellationToken cancellationToken)
    {
        _movieDetails = await _vodService.GetMovieDetailsAsync(item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Plot = _movieDetails?.Plot;
        Cast = _movieDetails?.Cast;
        Director = _movieDetails?.Director;
        _hasProviderBackdrop = !string.IsNullOrWhiteSpace(_movieDetails?.BackdropUrl);
        BackdropUrl = _movieDetails?.BackdropUrl ?? item.PosterUrl;

        var chips = new List<string>();
        if (item.Year is { } year)
        {
            chips.Add(year.ToString(CultureInfo.CurrentCulture));
        }

        if (_movieDetails?.DurationSeconds is { } secs and > 0)
        {
            // Not TimeSpan.ToString: unescaped spaces in TimeSpan custom formats throw.
            var duration = TimeSpan.FromSeconds(secs);
            chips.Add($"{(int)duration.TotalHours}h {duration.Minutes}m");
        }

        if ((_movieDetails?.Rating ?? item.Rating) is { } rating and > 0)
        {
            chips.Add($"★ {rating:0.0}");
        }

        if (!string.IsNullOrWhiteSpace(_movieDetails?.Genre))
        {
            chips.Add(_movieDetails.Genre);
        }

        MetadataChips = new ObservableCollection<string>(chips);

        // Resume prompt.
        if (_session.CurrentProfile is { } profile)
        {
            _resumeEntry = await _watchHistory.GetAsync(
                profile.Id, ContentKind.Movie, item.ProviderItemId, cancellationToken);
            if (_resumeEntry is { PositionSeconds: > 30 })
            {
                CanResume = true;
                ResumeLabel = $"Resume from {TimeSpan.FromSeconds(_resumeEntry.PositionSeconds):h\\:mm\\:ss}";
            }
        }
    }

    private async Task LoadSeriesAsync(VodItem item, CancellationToken cancellationToken)
    {
        var details = await _vodService.GetSeriesDetailsAsync(item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Plot = details?.Plot;
        Cast = details?.Cast;
        Director = details?.Director;
        _hasProviderBackdrop = !string.IsNullOrWhiteSpace(details?.BackdropUrl);
        BackdropUrl = details?.BackdropUrl ?? item.PosterUrl;

        var seasons = details?.Seasons ?? [];
        var episodeTotal = seasons.Sum(s => s.Episodes.Count);

        var chips = new List<string>();
        if (item.Year is { } year)
        {
            chips.Add(year.ToString(CultureInfo.CurrentCulture));
        }

        if (seasons.Count > 0)
        {
            chips.Add(seasons.Count == 1
                ? Strings.Vod_OneSeason
                : Strings.Format(Strings.Vod_SeasonsCountFormat, seasons.Count));
        }

        if (episodeTotal > 0)
        {
            chips.Add(episodeTotal == 1
                ? Strings.Vod_OneEpisode
                : Strings.Format(Strings.Vod_EpisodesCountFormat, episodeTotal));
        }

        if ((details?.Rating ?? item.Rating) is { } rating and > 0)
        {
            chips.Add($"★ {rating:0.0}");
        }

        if (!string.IsNullOrWhiteSpace(details?.Genre))
        {
            chips.Add(details.Genre);
        }

        MetadataChips = new ObservableCollection<string>(chips);

        var profile = _session.CurrentProfile;
        IReadOnlyList<WatchHistoryEntry> recent = profile is null
            ? []
            : await _watchHistory.GetRecentAsync(profile.Id, 500, cancellationToken);

        // Most recent entry per episode key (the list arrives newest-first).
        var history = recent
            .Where(h => h.ItemKind == ContentKind.Series)
            .GroupBy(h => h.ItemKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var season in seasons)
        {
            var rows = season.Episodes.Select(episode =>
            {
                var row = new EpisodeRow { Episode = episode };
                if (history.TryGetValue(EpisodeKey(item, episode), out var entry))
                {
                    row.ResumeProgress = entry.Progress * 100;
                }

                return row;
            }).ToList();

            Seasons.Add(new SeasonGroup { Number = season.Number, Episodes = rows });
        }

        ResolveSeriesNextUp(item, recent);
    }

    /// <summary>
    /// Picks the hero action for a series: resume the most recently watched episode, advance
    /// to the one after it when it finished, or start from the first episode when unwatched.
    /// Also lands the tab strip on the season that episode belongs to.
    /// </summary>
    private void ResolveSeriesNextUp(VodItem item, IReadOnlyList<WatchHistoryEntry> recent)
    {
        var allRows = Seasons.SelectMany(s => s.Episodes).ToList();
        if (allRows.Count == 0)
        {
            return;
        }

        EpisodeRow? nextUp = null;
        string? label = null;

        var prefix = item.ProviderItemId + ":";
        var latest = recent.FirstOrDefault(h =>
            h.ItemKind == ContentKind.Series && h.ItemKey.StartsWith(prefix, StringComparison.Ordinal));
        if (latest is not null)
        {
            var episodeId = latest.ItemKey[prefix.Length..];
            var index = allRows.FindIndex(r => r.Episode.ProviderEpisodeId == episodeId);
            if (index >= 0)
            {
                if (latest.Progress < CompletedProgress)
                {
                    nextUp = allRows[index];
                    label = latest.PositionSeconds > 30
                        ? Strings.Format(
                            Strings.Vod_ResumeEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number)
                        : Strings.Format(
                            Strings.Vod_PlayEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number);
                }
                else if (index + 1 < allRows.Count)
                {
                    nextUp = allRows[index + 1];
                    label = Strings.Format(
                        Strings.Vod_PlayEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number);
                }
            }
        }

        nextUp ??= allRows[0];
        _seriesNextUp = nextUp;
        SeriesPlayLabel = label ?? Strings.Vod_Play;
        SelectedSeason = Seasons.FirstOrDefault(s => s.Episodes.Contains(nextUp)) ?? Seasons[0];
    }

    private static string EpisodeKey(VodItem series, SeriesEpisode episode) =>
        $"{series.ProviderItemId}:{episode.ProviderEpisodeId}";

    [RelayCommand]
    private void Back() => _navigation.GoBack();

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (_item is null || IsSeries)
        {
            return;
        }

        var url = _vodService.ResolveMovieUrl(_item, _movieDetails?.ContainerExtension);
        if (url is null)
        {
            return;
        }

        await _playback.PlayVodAsync(new VodPlayRequest(
            url, ContentKind.Movie, _item.ProviderItemId, _item.Name, _item.PosterUrl, ResumeSeconds: 0),
            CancellationToken.None);
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_item is null || _resumeEntry is null)
        {
            return;
        }

        var url = _vodService.ResolveMovieUrl(_item, _movieDetails?.ContainerExtension);
        if (url is null)
        {
            return;
        }

        await _playback.PlayVodAsync(new VodPlayRequest(
            url, ContentKind.Movie, _item.ProviderItemId, _item.Name, _item.PosterUrl,
            _resumeEntry.PositionSeconds), CancellationToken.None);
    }

    /// <summary>Hero action for series: plays the resolved next-up episode.</summary>
    [RelayCommand]
    private Task PlaySeriesAsync() => PlayEpisodeAsync(_seriesNextUp);

    [RelayCommand]
    private async Task PlayEpisodeAsync(EpisodeRow? row)
    {
        if (row is null || _item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        var url = _vodService.ResolveEpisodeUrl(row.Episode);
        if (url is null)
        {
            return;
        }

        var key = EpisodeKey(_item, row.Episode);
        var existing = await _watchHistory.GetAsync(profile.Id, ContentKind.Series, key, CancellationToken.None);
        var resume = existing?.PositionSeconds ?? 0;

        await _playback.PlayVodAsync(new VodPlayRequest(
            url, ContentKind.Series, key,
            $"{_item.Name} · S{row.Episode.Season}E{row.Episode.Number}", _item.PosterUrl, resume),
            CancellationToken.None);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        if (IsFavorite)
        {
            await _favorites.RemoveAsync(profile.Id, _item.Kind, _item.ProviderItemId, CancellationToken.None);
            IsFavorite = false;
        }
        else
        {
            await _favorites.AddAsync(
                profile.Id, _item.Kind, _item.ProviderItemId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CancellationToken.None);
            IsFavorite = true;
        }
    }
}
