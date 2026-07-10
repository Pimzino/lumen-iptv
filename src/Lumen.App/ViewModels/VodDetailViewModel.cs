using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.App.Services.Downloads;
using Lumen.App.Services.Playback;
using Lumen.App.Services.Trakt;
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

    /// <summary>True once the episode was watched to completion (auto, manual, or Trakt).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchedToggleTooltip))]
    private bool _isWatched;

    public string WatchedToggleTooltip => IsWatched
        ? Strings.Vod_MarkEpisodeUnwatched
        : Strings.Vod_MarkEpisodeWatched;

    /// <summary>The download for this episode, when one exists; drives the card's download button.</summary>
    [ObservableProperty]
    private DownloadRow? _download;
}

/// <summary>A season tab on a series detail page.</summary>
public sealed partial class SeasonGroup : ObservableObject
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWatched))]
    [NotifyPropertyChangedFor(nameof(AllWatched))]
    [NotifyPropertyChangedFor(nameof(WatchedCountLabel))]
    private int _watchedCount;

    public bool HasWatched => WatchedCount > 0;

    public bool AllWatched => Episodes.Count > 0 && WatchedCount == Episodes.Count;

    /// <summary>"3/10 watched" under the season tab.</summary>
    public string WatchedCountLabel => Strings.Format(Strings.Vod_WatchedCountFormat, WatchedCount, Episodes.Count);

    public void RefreshWatchedCount() => WatchedCount = Episodes.Count(e => e.IsWatched);
}

/// <summary>
/// Movie or series detail page: backdrop, poster, plot, metadata chips, resume support, and
/// (for series) season tabs over an episode-card list. Series get a smart primary action that
/// resumes the most recently watched episode or advances to the next one.
/// </summary>
public sealed partial class VodDetailViewModel : ObservableObject, INavigationAware,
    IRecipient<TraktSyncCompletedMessage>, IRecipient<WatchProgressSavedMessage>,
    IRecipient<DownloadStateChangedMessage>, IRecipient<DownloadRemovedMessage>
{
    /// <summary>Progress beyond which an episode counts as watched and the next one is queued.</summary>
    private const double CompletedProgress = 0.95;

    private readonly VodService _vodService;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IFavoritesRepository _favorites;
    private readonly ICatalogRepository _catalog;
    private readonly ISessionService _session;
    private readonly PlaybackService _playback;
    private readonly INavigationService _navigation;
    private readonly ArtworkService _artwork;
    private readonly TraktSyncService _traktSync;
    private readonly Services.Downloads.DownloadService _downloads;

    private VodItem? _item;
    private MovieDetails? _movieDetails;
    private SeriesDetails? _seriesDetails;
    private WatchHistoryEntry? _resumeEntry;
    private EpisodeRow? _seriesNextUp;
    private bool _hasProviderBackdrop;

    /// <summary>Episode rows by watch-history key, for O(1) live progress updates.</summary>
    private Dictionary<string, EpisodeRow> _rowsByKey = new(StringComparer.Ordinal);

    public VodDetailViewModel(
        VodService vodService,
        IWatchHistoryRepository watchHistory,
        IFavoritesRepository favorites,
        ICatalogRepository catalog,
        ISessionService session,
        PlaybackService playback,
        INavigationService navigation,
        ArtworkService artwork,
        TraktSyncService traktSync,
        Services.Downloads.DownloadService downloads,
        IMessenger messenger)
    {
        _vodService = vodService;
        _watchHistory = watchHistory;
        _favorites = favorites;
        _catalog = catalog;
        _session = session;
        _playback = playback;
        _navigation = navigation;
        _artwork = artwork;
        _traktSync = traktSync;
        _downloads = downloads;
        messenger.RegisterAll(this);
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

    /// <summary>The movie's download, when one exists; the Download button reflects its live state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovieDownloadLabel))]
    [NotifyPropertyChangedFor(nameof(IsMovieDownloadEnabled))]
    private DownloadRow? _movieDownload;

    /// <summary>Download-button caption for a movie: "Download", "Downloading 42%", "Downloaded", etc.</summary>
    public string MovieDownloadLabel => MovieDownload?.ButtonLabel ?? Strings.Downloads_Download;

    /// <summary>The button is inert while queued/downloading; actionable otherwise.</summary>
    public bool IsMovieDownloadEnabled => MovieDownload is null
        || MovieDownload.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Paused;

    partial void OnMovieDownloadChanged(DownloadRow? oldValue, DownloadRow? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnMovieDownloadRowChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnMovieDownloadRowChanged;
        }
    }

    private void OnMovieDownloadRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(MovieDownloadLabel));
        OnPropertyChanged(nameof(IsMovieDownloadEnabled));
    }

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private string? _resumeLabel;

    /// <summary>Movie watched state (series watched state lives on the episode rows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkWatchedLabel))]
    private bool _isWatched;

    /// <summary>"Watched · 12 Mar 2026" chip text; null when unwatched.</summary>
    [ObservableProperty]
    private string? _watchedLabel;

    public string MarkWatchedLabel => IsWatched ? Strings.Vod_MarkUnwatched : Strings.Vod_MarkWatched;

    /// <summary>Whole-series watched fraction 0–100 for the hero poster bar (0 for movies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeriesProgress))]
    private double _seriesProgress;

    public bool HasSeriesProgress => SeriesProgress > 0;

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
        _seriesDetails = null;
        Title = item.Name;
        PosterUrl = item.PosterUrl;
        IsSeries = item.Kind == ContentKind.Series;
        IsLoading = true;
        HasEpisodes = false;
        ShowEmptyEpisodes = false;
        SelectedSeason = null;
        Seasons.Clear();
        MetadataChips = [];
        IsWatched = false;
        WatchedLabel = null;
        SeriesProgress = 0;
        MovieDownload = null;
        _rowsByKey = new Dictionary<string, EpisodeRow>(StringComparer.Ordinal);

        // The full player is an overlay — this page stays alive underneath it, so the row of
        // whatever is playing ticks its bar from live playback instead of waiting for a reload.
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _playback.PropertyChanged += OnPlaybackPropertyChanged;

        var profile = _session.CurrentProfile;
        if (profile is not null)
        {
            var favorites = await _favorites.GetAllAsync(profile.Id, cancellationToken);
            IsFavorite = favorites.Any(f => f.ItemKind == item.Kind && f.ItemKey == item.ProviderItemId);
        }

        try
        {
            // Load this profile's downloads so the movie/episode buttons reflect their state.
            if (profile is not null)
            {
                await _downloads.EnsureLoadedAsync(profile.Id, cancellationToken);
            }

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
        try
        {
            // Exact-URL probe: this is the hero image and the view fetches it regardless,
            // so a dead or junk provider poster is swapped for external artwork.
            var resolvedPoster = await _artwork.ResolvePosterAsync(
                item.Kind, PosterUrl, item.Name, item.Year,
                probeExactUrl: true, cancellationToken);
            if (!ReferenceEquals(_item, item))
            {
                return;
            }

            if (!string.Equals(resolvedPoster, PosterUrl, StringComparison.Ordinal))
            {
                PosterUrl = resolvedPoster;
                // Plays, downloads, and watch-history stamps read the model's poster —
                // keep it in step so they don't propagate the dead provider URL.
                item.PosterUrl = resolvedPoster;
            }

            if (_hasProviderBackdrop)
            {
                return;
            }

            var art = await _artwork.GetArtworkAsync(item.Kind, item.Name, item.Year, cancellationToken);
            if (art?.BackdropUrl is not null && ReferenceEquals(_item, item))
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
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;

        // Unsubscribe from the service-owned download row so this transient page can be collected
        // (the row lives in the DownloadService for the app's lifetime).
        MovieDownload = null;
    }

    /// <summary>Ticks the playing episode's bar (and the hero fraction) once a second.</summary>
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaybackService.PositionSeconds)
            || !_playback.IsVod
            || _playback.DurationSeconds <= 0
            || _playback.CurrentVod is not { } vod)
        {
            return;
        }

        var progress = Math.Clamp(_playback.PositionSeconds / _playback.DurationSeconds * 100, 0, 100);
        if (IsSeries && _rowsByKey.TryGetValue(vod.ItemKey, out var row))
        {
            row.ResumeProgress = progress;
            RefreshSeriesProgress();
        }
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

        // Resume prompt + watched state.
        if (_session.CurrentProfile is { } profile)
        {
            _resumeEntry = await _watchHistory.GetAsync(
                profile.Id, ContentKind.Movie, item.ProviderItemId, cancellationToken);
            if (_resumeEntry is { PositionSeconds: > 30 })
            {
                CanResume = true;
                ResumeLabel = $"Resume from {TimeSpan.FromSeconds(_resumeEntry.PositionSeconds):h\\:mm\\:ss}";
            }

            IsWatched = _resumeEntry?.Completed == true;
            WatchedLabel = BuildWatchedLabel(_resumeEntry);
            MovieDownload = FindLoadedRow(profile.Id, ContentKind.Movie, item.ProviderItemId);
        }

        // Trakt may know this movie was watched elsewhere; check quietly after the page shows.
        _ = ReconcileMovieWithTraktAsync(item, cancellationToken);
    }

    /// <summary>"Watched · 12 Mar 2026" (or plain "Watched" without a completion date).</summary>
    private static string? BuildWatchedLabel(WatchHistoryEntry? entry)
    {
        if (entry is not { Completed: true })
        {
            return null;
        }

        return entry.CompletedUtc is { } completedUtc
            ? Strings.Format(
                Strings.Vod_WatchedOnFormat,
                DateTimeOffset.FromUnixTimeSeconds(completedUtc).ToLocalTime().ToString(
                    "d MMM yyyy", CultureInfo.CurrentCulture))
            : Strings.Vod_Watched;
    }

    private async Task LoadSeriesAsync(VodItem item, CancellationToken cancellationToken)
    {
        var details = await _vodService.GetSeriesDetailsAsync(item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _seriesDetails = details;

        Plot = details?.Plot;
        Cast = details?.Cast;
        Director = details?.Director;
        _hasProviderBackdrop = !string.IsNullOrWhiteSpace(details?.BackdropUrl);
        BackdropUrl = details?.BackdropUrl ?? item.PosterUrl;

        var seasons = details?.Seasons ?? [];
        var episodeTotal = seasons.Sum(s => s.Episodes.Count);

        // Cache the total so the library grid can draw this series' watched-fraction bar.
        if (episodeTotal > 0 && item.EpisodeTotal != episodeTotal)
        {
            item.EpisodeTotal = episodeTotal;
            _ = _catalog.SetSeriesEpisodeTotalAsync(item.Id, episodeTotal, CancellationToken.None);
        }

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
        IReadOnlyList<WatchHistoryEntry> seriesHistory = profile is null
            ? []
            : await _watchHistory.GetForSeriesAsync(profile.Id, item.ProviderItemId, cancellationToken);

        // One row per episode key (unique constraint).
        var history = seriesHistory.ToDictionary(h => h.ItemKey, StringComparer.Ordinal);

        foreach (var season in seasons)
        {
            var rows = season.Episodes.Select(episode =>
            {
                var row = new EpisodeRow { Episode = episode };
                var key = EpisodeKey(item, episode);
                if (history.TryGetValue(key, out var entry))
                {
                    // Finished episodes show a full bar (their stored position is 0).
                    row.ResumeProgress = entry.Completed ? 100 : entry.Progress * 100;
                    row.IsWatched = entry.Completed;
                }

                row.Download = profile is null ? null : FindLoadedRow(profile.Id, ContentKind.Series, key);
                _rowsByKey[key] = row;
                return row;
            }).ToList();

            var group = new SeasonGroup { Number = season.Number, Episodes = rows };
            group.RefreshWatchedCount();
            Seasons.Add(group);
        }

        ResolveSeriesNextUp(item, seriesHistory, moveSelection: true);
        RefreshSeriesProgress();

        // Episodes watched elsewhere land here: this is the only moment season/episode numbers
        // meet provider episode ids, so Trakt episode reconciliation runs off the loaded details.
        if (details is not null)
        {
            _ = ReconcileSeriesWithTraktAsync(item, details, cancellationToken);
        }
    }

    /// <summary>Whole-series fraction: watched episodes count 1, in-progress ones their fraction.</summary>
    private void RefreshSeriesProgress()
    {
        var rows = Seasons.SelectMany(s => s.Episodes).ToList();
        if (rows.Count == 0)
        {
            SeriesProgress = 0;
            return;
        }

        var units = rows.Sum(r => Math.Clamp(r.ResumeProgress, 0, 100) / 100);
        SeriesProgress = units / rows.Count * 100;
    }

    /// <summary>Marks the open movie watched when the Trakt snapshot says so, then updates the chip.</summary>
    private async Task ReconcileMovieWithTraktAsync(VodItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (_session.CurrentProfile is not { } profile)
            {
                return;
            }

            var changed = await _traktSync.ReconcileMovieAsync(profile.Id, item, _movieDetails, cancellationToken);
            if (!changed || !ReferenceEquals(_item, item))
            {
                return;
            }

            _resumeEntry = await _watchHistory.GetAsync(
                profile.Id, ContentKind.Movie, item.ProviderItemId, cancellationToken);
            IsWatched = _resumeEntry?.Completed == true;
            WatchedLabel = BuildWatchedLabel(_resumeEntry);
            CanResume = _resumeEntry is { PositionSeconds: > 30 };
            if (!CanResume)
            {
                ResumeLabel = null;
            }
        }
        catch (OperationCanceledException)
        {
            // navigated away
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Trakt movie reconcile failed");
        }
    }

    /// <summary>Pulls Trakt-watched episodes into the open series page.</summary>
    private async Task ReconcileSeriesWithTraktAsync(
        VodItem item, SeriesDetails details, CancellationToken cancellationToken)
    {
        try
        {
            if (_session.CurrentProfile is not { } profile)
            {
                return;
            }

            var changed = await _traktSync.ReconcileSeriesAsync(profile.Id, item, details, cancellationToken);
            if (changed > 0 && ReferenceEquals(_item, item))
            {
                await ApplySeriesHistoryAsync(item, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // navigated away
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Trakt series reconcile failed");
        }
    }

    /// <summary>Re-reads the series' history and refreshes rows, season counts, and next-up.</summary>
    private async Task ApplySeriesHistoryAsync(VodItem item, CancellationToken cancellationToken)
    {
        if (_session.CurrentProfile is not { } profile)
        {
            return;
        }

        var history = await _watchHistory.GetForSeriesAsync(profile.Id, item.ProviderItemId, cancellationToken);
        var map = history.ToDictionary(h => h.ItemKey, StringComparer.Ordinal);
        foreach (var row in Seasons.SelectMany(s => s.Episodes))
        {
            if (map.TryGetValue(EpisodeKey(item, row.Episode), out var entry))
            {
                row.IsWatched = entry.Completed;
                row.ResumeProgress = entry.Completed ? 100 : entry.Progress * 100;
            }
        }

        foreach (var season in Seasons)
        {
            season.RefreshWatchedCount();
        }

        RefreshSeriesProgress();
        ResolveSeriesNextUp(item, history, moveSelection: false);
    }

    /// <summary>A background sync finished — refresh whatever page is open.</summary>
    public void Receive(TraktSyncCompletedMessage message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => _ = RefreshAfterTraktSyncAsync());
    }

    /// <summary>
    /// Playback banked progress (pause, stop, finish, or switching titles) — reflect it on the
    /// open page immediately; the player overlay never re-navigates this page.
    /// </summary>
    public void Receive(WatchProgressSavedMessage message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => ApplySavedProgress(message.Entry));
    }

    private void ApplySavedProgress(WatchHistoryEntry entry)
    {
        if (_item is not { } item || entry.ProfileId != _session.CurrentProfile?.Id)
        {
            return;
        }

        if (!IsSeries && entry.ItemKind == ContentKind.Movie && entry.ItemKey == item.ProviderItemId)
        {
            _resumeEntry = entry;
            CanResume = entry.PositionSeconds > 30;
            ResumeLabel = CanResume
                ? $"Resume from {TimeSpan.FromSeconds(entry.PositionSeconds):h\\:mm\\:ss}"
                : null;

            // The entry is a write payload: it can set the watched chip but never clears it
            // (the store merges, so a mid-rewatch save leaves the flag alone).
            if (entry.Completed)
            {
                IsWatched = true;
                WatchedLabel = BuildWatchedLabel(entry);
            }

            return;
        }

        if (IsSeries && entry.ItemKind == ContentKind.Series && _rowsByKey.TryGetValue(entry.ItemKey, out var row))
        {
            row.ResumeProgress = entry.Completed ? 100 : entry.Progress * 100;
            if (entry.Completed && !row.IsWatched)
            {
                row.IsWatched = true;
            }

            // Counts, the hero fraction, and next-up all shift when an episode completes.
            _ = RefreshSeriesWatchedStateAsync();
        }
    }

    private async Task RefreshAfterTraktSyncAsync()
    {
        try
        {
            if (_item is not { } item || _session.CurrentProfile is not { } profile)
            {
                return;
            }

            if (IsSeries)
            {
                await ApplySeriesHistoryAsync(item, CancellationToken.None);
                if (_seriesDetails is { } details)
                {
                    await ReconcileSeriesWithTraktAsync(item, details, CancellationToken.None);
                }
            }
            else
            {
                _resumeEntry = await _watchHistory.GetAsync(
                    profile.Id, ContentKind.Movie, item.ProviderItemId, CancellationToken.None);
                IsWatched = _resumeEntry?.Completed == true;
                WatchedLabel = BuildWatchedLabel(_resumeEntry);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Refreshing watched state after a Trakt sync failed");
        }
    }

    /// <summary>
    /// Picks the hero action for a series: resume the most recently touched episode when it's
    /// mid-play, otherwise advance to the first unwatched episode after it. Falls back to the
    /// first episode when nothing was watched (or everything was). Optionally lands the tab
    /// strip on that episode's season — skipped on in-page refreshes after a manual toggle so
    /// the user's tab doesn't jump.
    /// </summary>
    private void ResolveSeriesNextUp(VodItem item, IReadOnlyList<WatchHistoryEntry> seriesHistory, bool moveSelection)
    {
        var allRows = Seasons.SelectMany(s => s.Episodes).ToList();
        if (allRows.Count == 0)
        {
            return;
        }

        EpisodeRow? nextUp = null;
        string? label = null;

        var prefix = item.ProviderItemId + ":";
        var latest = seriesHistory.OrderByDescending(h => h.WatchedUtc).FirstOrDefault();
        if (latest is not null)
        {
            var episodeId = latest.ItemKey[prefix.Length..];
            var index = allRows.FindIndex(r => r.Episode.ProviderEpisodeId == episodeId);
            if (index >= 0)
            {
                if (!latest.Completed && latest.Progress < CompletedProgress)
                {
                    nextUp = allRows[index];
                    label = latest.PositionSeconds > 30
                        ? Strings.Format(
                            Strings.Vod_ResumeEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number)
                        : Strings.Format(
                            Strings.Vod_PlayEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number);
                }
                else
                {
                    // Finished the latest episode: queue the next unwatched one after it.
                    var candidate = allRows.Skip(index + 1).FirstOrDefault(r => !r.IsWatched);
                    if (candidate is not null)
                    {
                        nextUp = candidate;
                        label = Strings.Format(
                            Strings.Vod_PlayEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number);
                    }
                }
            }
        }

        // Nothing in flight: start at the first unwatched episode (first overall on a rewatch).
        if (nextUp is null)
        {
            nextUp = allRows.FirstOrDefault(r => !r.IsWatched) ?? allRows[0];
            if (!ReferenceEquals(nextUp, allRows[0]))
            {
                label = Strings.Format(Strings.Vod_PlayEpisodeFormat, nextUp.Episode.Season, nextUp.Episode.Number);
            }
        }

        _seriesNextUp = nextUp;
        SeriesPlayLabel = label ?? Strings.Vod_Play;
        if (moveSelection)
        {
            SelectedSeason = Seasons.FirstOrDefault(s => s.Episodes.Contains(nextUp)) ?? Seasons[0];
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
            $"{_item.Name} · S{row.Episode.Season}E{row.Episode.Number}", _item.PosterUrl, resume,
            row.Episode.Season, row.Episode.Number),
            CancellationToken.None);
    }

    /// <summary>Movie-page "Mark watched"/"Mark unwatched" toggle. Watched marks clear the resume point.</summary>
    [RelayCommand]
    private async Task ToggleWatchedAsync()
    {
        if (_item is null || IsSeries || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        var target = !IsWatched;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new WatchHistoryEntry
        {
            ProfileId = profile.Id,
            ItemKind = ContentKind.Movie,
            ItemKey = _item.ProviderItemId,
            Title = _item.Name,
            PosterUrl = _item.PosterUrl,
            DurationSeconds = _resumeEntry?.DurationSeconds ?? _movieDetails?.DurationSeconds ?? 0,
            WatchedUtc = now,
            Completed = target,
            PlayCount = 1,
            CompletedUtc = now,
        };
        await _watchHistory.SetCompletedAsync(entry, target, CancellationToken.None);

        // Both directions zero the stored position, so the resume prompt goes away either way.
        _resumeEntry = target ? entry : null;
        CanResume = false;
        ResumeLabel = null;
        IsWatched = target;
        WatchedLabel = BuildWatchedLabel(target ? entry : null);

        _ = PushToggleToTraktAsync(_item, entry, target, _movieDetails?.TmdbId, _movieDetails?.ImdbId);
    }

    /// <summary>Per-episode watched toggle (the small check button on an episode card).</summary>
    [RelayCommand]
    private async Task ToggleEpisodeWatchedAsync(EpisodeRow? row)
    {
        if (row is null || _item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        var target = !row.IsWatched;
        var entry = BuildEpisodeEntry(profile.Id, _item, row);
        await _watchHistory.SetCompletedAsync(entry, target, CancellationToken.None);
        row.IsWatched = target;
        row.ResumeProgress = target ? 100 : 0;

        _ = PushToggleToTraktAsync(_item, entry, target, _seriesDetails?.TmdbId, _seriesDetails?.ImdbId);
        await RefreshSeriesWatchedStateAsync();
    }

    /// <summary>Marks every remaining episode of a season watched.</summary>
    [RelayCommand]
    private async Task MarkSeasonWatchedAsync(SeasonGroup? season)
    {
        if (season is null || _item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        foreach (var row in season.Episodes.Where(r => !r.IsWatched))
        {
            var entry = BuildEpisodeEntry(profile.Id, _item, row);
            await _watchHistory.SetCompletedAsync(entry, true, CancellationToken.None);
            row.IsWatched = true;
            row.ResumeProgress = 100;
            _ = PushToggleToTraktAsync(_item, entry, true, _seriesDetails?.TmdbId, _seriesDetails?.ImdbId);
        }

        await RefreshSeriesWatchedStateAsync();
    }

    /// <summary>Best-effort Trakt history add/remove for a manual toggle (local state already saved).</summary>
    private async Task PushToggleToTraktAsync(
        VodItem item, WatchHistoryEntry entry, bool watched, long? tmdbId, string? imdbId)
    {
        try
        {
            if (_session.CurrentProfile is { } profile)
            {
                await _traktSync.PushManualToggleAsync(
                    profile.Id, item, entry, watched, tmdbId, imdbId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Trakt history push for a manual toggle failed");
        }
    }

    private static WatchHistoryEntry BuildEpisodeEntry(long profileId, VodItem series, EpisodeRow row)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new WatchHistoryEntry
        {
            ProfileId = profileId,
            ItemKind = ContentKind.Series,
            ItemKey = EpisodeKey(series, row.Episode),
            Title = $"{series.Name} · S{row.Episode.Season}E{row.Episode.Number}",
            PosterUrl = series.PosterUrl,
            DurationSeconds = row.Episode.DurationSeconds ?? 0,
            WatchedUtc = now,
            PlayCount = 1,
            CompletedUtc = now,
            Season = row.Episode.Season,
            EpisodeNumber = row.Episode.Number,
        };
    }

    /// <summary>Recomputes season tab counts, the series bar, and the hero next-up after toggles.</summary>
    private async Task RefreshSeriesWatchedStateAsync()
    {
        foreach (var season in Seasons)
        {
            season.RefreshWatchedCount();
        }

        RefreshSeriesProgress();
        if (_item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        var history = await _watchHistory.GetForSeriesAsync(profile.Id, _item.ProviderItemId, CancellationToken.None);
        ResolveSeriesNextUp(_item, history, moveSelection: false);
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

    // ---- Downloads -------------------------------------------------------------------------

    private DownloadRow? FindLoadedRow(long profileId, ContentKind kind, string itemKey) =>
        _downloads.Downloads.FirstOrDefault(
            r => r.Item.ProfileId == profileId && r.Kind == kind && r.ItemKey == itemKey);

    /// <summary>True when the source is HLS (.m3u8) — recorded rather than downloaded as a file.</summary>
    private static bool IsHlsSource(string? containerExtension, string? streamUrl)
    {
        if (string.Equals(containerExtension?.Trim().TrimStart('.'), "m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return streamUrl is not null
            && streamUrl.Split('?')[0].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private DownloadRequest BuildMovieRequest(long profileId)
    {
        var container = _movieDetails?.ContainerExtension ?? _item!.ContainerExtension;
        return new DownloadRequest(
            ContentKind.Movie, _item!.ProviderItemId, null, _item.ProviderItemId, container, _item.StreamUrl,
            _item.Name, _item.PosterUrl, null, null, IsHlsSource(container, _item.StreamUrl), profileId);
    }

    private DownloadRequest BuildEpisodeRequest(long profileId, EpisodeRow row)
    {
        var key = EpisodeKey(_item!, row.Episode);
        return new DownloadRequest(
            ContentKind.Series, key, _item!.ProviderItemId, row.Episode.ProviderEpisodeId,
            row.Episode.ContainerExtension, null,
            $"{_item.Name} · S{row.Episode.Season}E{row.Episode.Number}", _item.PosterUrl,
            row.Episode.Season, row.Episode.Number, IsHlsSource(row.Episode.ContainerExtension, null), profileId);
    }

    /// <summary>Movie Download button: enqueue, resume, retry, or play the finished file.</summary>
    [RelayCommand]
    private async Task DownloadMovieAsync()
    {
        if (_item is null || IsSeries || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        if (MovieDownload is { } existing)
        {
            await ActOnExistingDownloadAsync(existing);
            return;
        }

        MovieDownload = await _downloads.EnqueueAsync(BuildMovieRequest(profile.Id), CancellationToken.None);
    }

    /// <summary>Per-episode download button: enqueue, resume, retry, or play the finished file.</summary>
    [RelayCommand]
    private async Task DownloadEpisodeAsync(EpisodeRow? row)
    {
        if (row is null || _item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        if (row.Download is { } existing)
        {
            await ActOnExistingDownloadAsync(existing);
            return;
        }

        row.Download = await _downloads.EnqueueAsync(BuildEpisodeRequest(profile.Id, row), CancellationToken.None);
    }

    /// <summary>Downloads every not-yet-downloaded (or failed) episode of a season.</summary>
    [RelayCommand]
    private async Task DownloadSeasonAsync(SeasonGroup? season)
    {
        if (season is null || _item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        foreach (var row in season.Episodes.Where(r => r.Download is null || r.Download.IsFailed))
        {
            row.Download = await _downloads.EnqueueAsync(BuildEpisodeRequest(profile.Id, row), CancellationToken.None);
        }
    }

    private async Task ActOnExistingDownloadAsync(DownloadRow download)
    {
        switch (download.Status)
        {
            case DownloadStatus.Completed:
                await PlayDownloadedAsync(download);
                break;
            case DownloadStatus.Failed:
                _downloads.Retry(download.Id);
                break;
            case DownloadStatus.Paused:
                _downloads.Resume(download.Id);
                break;
            default:
                break; // queued/downloading — no action
        }
    }

    private async Task PlayDownloadedAsync(DownloadRow download)
    {
        if (!download.CanPlay)
        {
            return;
        }

        double resume = 0;
        if (_session.CurrentProfile is { } profile)
        {
            var entry = await _watchHistory.GetAsync(profile.Id, download.Kind, download.ItemKey, CancellationToken.None);
            resume = entry?.PositionSeconds ?? 0;
        }

        var url = new Uri(download.FilePath).AbsoluteUri;
        await _playback.PlayVodAsync(
            new VodPlayRequest(
                url, download.Kind, download.ItemKey, download.Title, download.PosterUrl, resume,
                download.Season, download.EpisodeNumber),
            CancellationToken.None);
    }

    /// <summary>A download for an item on this page may have started elsewhere — associate its row.</summary>
    public void Receive(DownloadStateChangedMessage message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => AssociateDownload(message.ItemKey));
    }

    public void Receive(DownloadRemovedMessage message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => ClearDownload(message.DownloadId));
    }

    private void AssociateDownload(string itemKey)
    {
        if (_item is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        if (!IsSeries && itemKey == _item.ProviderItemId && MovieDownload is null)
        {
            MovieDownload = FindLoadedRow(profile.Id, ContentKind.Movie, itemKey);
        }
        else if (IsSeries && _rowsByKey.TryGetValue(itemKey, out var row) && row.Download is null)
        {
            row.Download = FindLoadedRow(profile.Id, ContentKind.Series, itemKey);
        }
    }

    private void ClearDownload(long downloadId)
    {
        if (MovieDownload?.Id == downloadId)
        {
            MovieDownload = null;
        }

        foreach (var row in _rowsByKey.Values)
        {
            if (row.Download?.Id == downloadId)
            {
                row.Download = null;
            }
        }
    }
}
