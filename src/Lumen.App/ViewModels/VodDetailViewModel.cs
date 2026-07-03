using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>An episode row on a series detail page.</summary>
public sealed partial class EpisodeRow : ObservableObject
{
    public required SeriesEpisode Episode { get; init; }

    public string Title => $"{Episode.Number}. {Episode.Title}";

    public string? Plot => Episode.Plot;

    public string? Duration => Episode.DurationSeconds is { } secs and > 0
        ? TimeSpan.FromSeconds(secs).ToString(secs >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : null;

    [ObservableProperty]
    private double _resumeProgress;
}

/// <summary>A season grouping of episodes on a series detail page.</summary>
public sealed class SeasonGroup
{
    public required int Number { get; init; }

    public required IReadOnlyList<EpisodeRow> Episodes { get; init; }

    public string Label => $"Season {Number}";
}

/// <summary>
/// Movie or series detail page: backdrop, poster, plot, metadata chips, resume support, and
/// (for series) seasons of episodes. Shows a resume prompt when a saved position exists.
/// </summary>
public sealed partial class VodDetailViewModel : ObservableObject, INavigationAware
{
    private readonly VodService _vodService;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IFavoritesRepository _favorites;
    private readonly ISessionService _session;
    private readonly PlaybackService _playback;
    private readonly INavigationService _navigation;

    private VodItem? _item;
    private MovieDetails? _movieDetails;
    private WatchHistoryEntry? _resumeEntry;

    public VodDetailViewModel(
        VodService vodService,
        IWatchHistoryRepository watchHistory,
        IFavoritesRepository favorites,
        ISessionService session,
        PlaybackService playback,
        INavigationService navigation)
    {
        _vodService = vodService;
        _watchHistory = watchHistory;
        _favorites = favorites;
        _session = session;
        _playback = playback;
        _navigation = navigation;
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
    private ObservableCollection<string> _metadataChips = [];

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private string? _resumeLabel;

    public ObservableCollection<SeasonGroup> Seasons { get; } = [];

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not VodItem item)
        {
            return;
        }

        _item = item;
        Title = item.Name;
        PosterUrl = item.PosterUrl;
        IsSeries = item.Kind == ContentKind.Series;
        IsLoading = true;
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
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadMovieAsync(VodItem item, CancellationToken cancellationToken)
    {
        _movieDetails = await _vodService.GetMovieDetailsAsync(item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Plot = _movieDetails?.Plot;
        BackdropUrl = _movieDetails?.BackdropUrl ?? item.PosterUrl;

        var chips = new List<string>();
        if (item.Year is { } year)
        {
            chips.Add(year.ToString(System.Globalization.CultureInfo.CurrentCulture));
        }

        if (_movieDetails?.DurationSeconds is { } secs and > 0)
        {
            chips.Add(TimeSpan.FromSeconds(secs).ToString(@"h\'h\' m\'m\'"));
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
        BackdropUrl = details?.BackdropUrl ?? item.PosterUrl;

        var chips = new List<string>();
        if (item.Year is { } year)
        {
            chips.Add(year.ToString(System.Globalization.CultureInfo.CurrentCulture));
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
        var history = profile is null
            ? new Dictionary<string, WatchHistoryEntry>()
            : (await _watchHistory.GetRecentAsync(profile.Id, 500, cancellationToken))
                .Where(h => h.ItemKind == ContentKind.Series)
                .GroupBy(h => h.ItemKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var season in details?.Seasons ?? [])
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
