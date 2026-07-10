using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Resources;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A "continue watching" tile (VOD with a resume position).</summary>
public sealed partial class ContinueCard : ObservableObject
{
    public required WatchHistoryEntry Entry { get; init; }

    public string Title => Entry.Title;

    public string? ImageUrl => Entry.PosterUrl;

    public double Progress => Entry.Progress * 100;

    public string Monogram => Entry.Title.Length > 0 ? Entry.Title[..1].ToUpperInvariant() : "?";

    /// <summary>"Movie · 2 h ago" — what it is and when it was left off.</summary>
    public required string MetaLine { get; init; }

    /// <summary>"1h 12m left" / "41m left" — from the stored resume position.</summary>
    public string? RemainingLabel
    {
        get
        {
            var remaining = Entry.DurationSeconds - Entry.PositionSeconds;
            if (Entry.DurationSeconds <= 0 || remaining <= 0)
            {
                return null;
            }

            var span = TimeSpan.FromSeconds(remaining);
            var duration = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{Math.Max(1, (int)span.TotalMinutes)}m";
            return Strings.Format(Strings.Home_TimeLeftFormat, duration);
        }
    }
}

/// <summary>A favorite-channel tile with its live now/next.</summary>
public sealed partial class FavoriteChannelCard : ObservableObject
{
    public required Channel Channel { get; init; }

    public string Name => Channel.Name;

    /// <summary>Playlist logo, or the mapped guide channel's icon when the playlist has none.</summary>
    [ObservableProperty]
    private string? _logoUrl;

    public string Monogram => Channel.Name.Length > 0 ? Channel.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    private string? _nowTitle;

    [ObservableProperty]
    private string? _nextTitle;

    [ObservableProperty]
    private double _nowProgress;
}

/// <summary>
/// A watch-history tile: a live channel or VOD title the profile actually played,
/// newest first. Live entries resolve their channel so a click replays instantly.
/// </summary>
public sealed partial class RecentWatchCard : ObservableObject
{
    public required WatchHistoryEntry Entry { get; init; }

    /// <summary>The live channel behind this entry; null for VOD (or a since-removed channel).</summary>
    public Channel? Channel { get; init; }

    public string Title => Entry.Title;

    public bool IsLive => Entry.ItemKind == ContentKind.Live;

    [ObservableProperty]
    private string? _imageUrl;

    public string Monogram => Entry.Title.Length > 0 ? Entry.Title[..1].ToUpperInvariant() : "?";

    /// <summary>"Live · 2 h ago" / "Movie · Yesterday".</summary>
    public required string MetaLine { get; init; }

    /// <summary>What the channel is airing right now (live entries with EPG only).</summary>
    [ObservableProperty]
    private string? _nowTitle;

    [ObservableProperty]
    private double _nowProgress;
}

/// <summary>
/// Home page: a time-aware greeting hero with the most recent resumable title ("Jump back
/// in"), then Continue watching, Recently watched (live + VOD history), Favorite channels
/// with live now/next, and Recently added movies/series. Rows load in parallel; the page
/// shows a designed empty state until the library is populated.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, INavigationAware,
    IRecipient<CatalogRefreshedMessage>
{
    private const int RowLimit = 20;
    private const int RecentWatchLimit = 12;

    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IFavoritesRepository _favorites;
    private readonly IEpgRepository _epg;
    private readonly IClock _clock;
    private readonly PlaybackServiceNavigator _playback;
    private readonly INavigationService _navigation;
    private readonly ArtworkService _artwork;

    public HomeViewModel(
        ISessionService session,
        ICatalogRepository catalog,
        IWatchHistoryRepository watchHistory,
        IFavoritesRepository favorites,
        IEpgRepository epg,
        IClock clock,
        PlaybackServiceNavigator playback,
        INavigationService navigation,
        ArtworkService artwork,
        IMessenger messenger)
    {
        _session = session;
        _catalog = catalog;
        _watchHistory = watchHistory;
        _favorites = favorites;
        _epg = epg;
        _clock = clock;
        _playback = playback;
        _navigation = navigation;
        _artwork = artwork;
        messenger.RegisterAll(this);
    }

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Flips true when all rows have landed; drives the staggered section reveal.</summary>
    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _profileName = string.Empty;

    /// <summary>Time-of-day salutation ("Good evening").</summary>
    [ObservableProperty]
    private string _greeting = string.Empty;

    /// <summary>Today, spelled out ("Friday 4 July").</summary>
    [ObservableProperty]
    private string _dateLine = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>The most recent resumable title, featured in the hero. Not repeated in the rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero))]
    private ContinueCard? _hero;

    public bool HasHero => Hero is not null;

    public ObservableCollection<ContinueCard> ContinueWatching { get; } = [];

    public ObservableCollection<RecentWatchCard> RecentlyWatched { get; } = [];

    public ObservableCollection<FavoriteChannelCard> FavoriteChannels { get; } = [];

    public ObservableCollection<VodCard> RecentMovies { get; } = [];

    public ObservableCollection<VodCard> RecentSeries { get; } = [];

    public bool HasContinueWatching => ContinueWatching.Count > 0;

    public bool HasRecentlyWatched => RecentlyWatched.Count > 0;

    public bool HasFavoriteChannels => FavoriteChannels.Count > 0;

    public bool HasRecentMovies => RecentMovies.Count > 0;

    public bool HasRecentSeries => RecentSeries.Count > 0;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        IsLoading = true;
        IsReady = false;
        var now = _clock.UtcNow.ToLocalTime();
        Greeting = now.Hour switch
        {
            >= 5 and < 12 => Strings.Home_GreetingMorning,
            >= 12 and < 18 => Strings.Home_GreetingAfternoon,
            >= 18 and < 23 => Strings.Home_GreetingEvening,
            _ => Strings.Home_GreetingNight,
        };
        DateLine = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoading = false;
            IsEmpty = true;
            return;
        }

        ProfileName = profile.Name;
        Hero = null;
        ContinueWatching.Clear();
        RecentlyWatched.Clear();
        FavoriteChannels.Clear();
        RecentMovies.Clear();
        RecentSeries.Clear();

        try
        {
            // Independent local queries — let the rows race; the page appears with the slowest.
            await Task.WhenAll(
                LoadContinueWatchingAsync(profile.Id, cancellationToken),
                LoadRecentlyWatchedAsync(profile.Id, cancellationToken),
                LoadFavoriteChannelsAsync(profile.Id, cancellationToken),
                LoadRecentAsync(profile.Id, ContentKind.Movie, RecentMovies, cancellationToken),
                LoadRecentAsync(profile.Id, ContentKind.Series, RecentSeries, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading Home failed");
        }

        NotifyRowFlags();
        IsEmpty = Hero is null && !HasContinueWatching && !HasRecentlyWatched
            && !HasFavoriteChannels && !HasRecentMovies && !HasRecentSeries;
        IsLoading = false;
        IsReady = !IsEmpty;
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadContinueWatchingAsync(long profileId, CancellationToken cancellationToken)
    {
        var recent = await _watchHistory.GetRecentAsync(profileId, RowLimit, cancellationToken);
        var resumable = recent
            .Where(e => e.ItemKind != ContentKind.Live
                && e.PositionSeconds > 30 && e.Progress is > 0.01 and < 0.95)
            .ToList();
        if (resumable.Count == 0)
        {
            return;
        }

        Hero = new ContinueCard { Entry = resumable[0], MetaLine = ContinueMeta(resumable[0]) };
        foreach (var entry in resumable.Skip(1))
        {
            ContinueWatching.Add(new ContinueCard { Entry = entry, MetaLine = ContinueMeta(entry) });
        }
    }

    private async Task LoadRecentlyWatchedAsync(long profileId, CancellationToken cancellationToken)
    {
        var recent = await _watchHistory.GetRecentAsync(profileId, RecentWatchLimit, cancellationToken);
        if (recent.Count == 0)
        {
            return;
        }

        var anyLive = recent.Any(e => e.ItemKind == ContentKind.Live);
        var logoFallbacks = anyLive
            ? await _artwork.GetChannelLogoFallbacksAsync(profileId, cancellationToken)
            : System.Collections.Immutable.ImmutableDictionary<long, string>.Empty;
        var mappings = anyLive
            ? (await _epg.GetMappingsAsync(profileId, cancellationToken)).ToDictionary(m => m.ChannelId, m => m.XmltvId)
            : new Dictionary<long, string>();

        var cards = new List<RecentWatchCard>(recent.Count);
        var xmltvByChannel = new Dictionary<long, string>();
        foreach (var entry in recent)
        {
            Channel? channel = null;
            if (entry.ItemKind == ContentKind.Live)
            {
                if (!long.TryParse(entry.ItemKey, out var channelId)
                    || await _catalog.GetChannelAsync(channelId, cancellationToken) is not { } resolved
                    || resolved.ProfileId != profileId)
                {
                    continue; // channel no longer exists — a dead card would be a broken promise
                }

                channel = resolved;
                var xmltvId = mappings.GetValueOrDefault(channel.Id) ?? channel.EpgChannelId;
                if (!string.IsNullOrEmpty(xmltvId))
                {
                    xmltvByChannel[channel.Id] = xmltvId;
                }
            }

            var card = new RecentWatchCard
            {
                Entry = entry,
                Channel = channel,
                MetaLine = $"{KindLabel(entry.ItemKind)} · {RelativeAge(entry.WatchedUtc)}",
                ImageUrl = channel is null
                    ? entry.PosterUrl
                    : string.IsNullOrWhiteSpace(channel.LogoUrl)
                        ? logoFallbacks.GetValueOrDefault(channel.Id)
                        : channel.LogoUrl,
            };
            cards.Add(card);
        }

        var nowNext = xmltvByChannel.Count == 0
            ? new Dictionary<string, NowNext>()
            : (IReadOnlyDictionary<string, NowNext>)await _epg.GetNowNextAsync(
                profileId, xmltvByChannel.Values.Distinct().ToList(),
                _clock.UtcNow.ToUnixTimeSeconds(), cancellationToken);

        foreach (var card in cards)
        {
            if (card.Channel is { } channel
                && xmltvByChannel.TryGetValue(channel.Id, out var xmltvId)
                && nowNext.TryGetValue(xmltvId, out var entry))
            {
                card.NowTitle = entry.Now?.Title;
                card.NowProgress = entry.Now?.ProgressAt(_clock.UtcNow) * 100 ?? 0;
            }

            RecentlyWatched.Add(card);
        }
    }

    private string ContinueMeta(WatchHistoryEntry entry) =>
        $"{KindLabel(entry.ItemKind)} · {RelativeAge(entry.WatchedUtc)}";

    private static string KindLabel(ContentKind kind) => kind switch
    {
        ContentKind.Live => Strings.Home_KindLive,
        ContentKind.Series => Strings.Home_KindSeries,
        _ => Strings.Home_KindMovie,
    };

    /// <summary>"Just now" / "12 min ago" / "5 h ago" / "Yesterday" / "3 days ago".</summary>
    private string RelativeAge(long watchedUtc)
    {
        var age = _clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(watchedUtc);
        return age switch
        {
            { TotalMinutes: < 1 } => Strings.Home_AgoJustNow,
            { TotalHours: < 1 } => Strings.Format(Strings.Home_AgoMinutesFormat, (int)age.TotalMinutes),
            { TotalHours: < 24 } => Strings.Format(Strings.Home_AgoHoursFormat, (int)age.TotalHours),
            { TotalHours: < 48 } => Strings.Home_AgoYesterday,
            _ => Strings.Format(Strings.Home_AgoDaysFormat, (int)age.TotalDays),
        };
    }

    private async Task LoadFavoriteChannelsAsync(long profileId, CancellationToken cancellationToken)
    {
        var favorites = await _favorites.GetAllAsync(profileId, cancellationToken);
        var channelIds = favorites.Where(f => f.ItemKind == ContentKind.Live)
            .Select(f => long.TryParse(f.ItemKey, out var id) ? id : -1)
            .Where(id => id >= 0)
            .ToHashSet();
        if (channelIds.Count == 0)
        {
            return;
        }

        var all = await _catalog.GetChannelsAsync(profileId, null, cancellationToken);
        var channels = all.Where(c => channelIds.Contains(c.Id)).Take(RowLimit).ToList();

        var mappings = (await _epg.GetMappingsAsync(profileId, cancellationToken))
            .ToDictionary(m => m.ChannelId, m => m.XmltvId);
        var logoFallbacks = await _artwork.GetChannelLogoFallbacksAsync(profileId, cancellationToken);
        var cards = new List<FavoriteChannelCard>();
        var xmltvIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            var card = new FavoriteChannelCard
            {
                Channel = channel,
                LogoUrl = string.IsNullOrWhiteSpace(channel.LogoUrl)
                    ? logoFallbacks.GetValueOrDefault(channel.Id)
                    : channel.LogoUrl,
            };
            cards.Add(card);
            var xmltvId = mappings.GetValueOrDefault(channel.Id) ?? channel.EpgChannelId;
            if (!string.IsNullOrEmpty(xmltvId))
            {
                xmltvIds.Add(xmltvId);
            }
        }

        var nowNext = xmltvIds.Count == 0
            ? new Dictionary<string, NowNext>()
            : (Dictionary<string, NowNext>)await _epg.GetNowNextAsync(
                profileId, xmltvIds.ToList(), _clock.UtcNow.ToUnixTimeSeconds(), cancellationToken);

        foreach (var card in cards)
        {
            var xmltvId = mappings.GetValueOrDefault(card.Channel.Id) ?? card.Channel.EpgChannelId;
            if (xmltvId is not null && nowNext.TryGetValue(xmltvId, out var entry))
            {
                card.NowTitle = entry.Now?.Title;
                card.NextTitle = entry.Next?.Title;
                card.NowProgress = entry.Now?.ProgressAt(_clock.UtcNow) * 100 ?? 0;
            }

            FavoriteChannels.Add(card);
        }
    }

    private async Task LoadRecentAsync(
        long profileId, ContentKind kind, ObservableCollection<VodCard> target, CancellationToken cancellationToken)
    {
        var items = await _catalog.GetRecentVodAsync(profileId, kind, RowLimit, cancellationToken);
        foreach (var item in items)
        {
            target.Add(new VodCard(item));
        }

        _ = EnrichPostersAsync(kind, target.ToList(), cancellationToken);
    }

    private async Task EnrichPostersAsync(
        ContentKind kind, IReadOnlyList<VodCard> cards, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var card in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = await _artwork.ResolvePosterAsync(
                    kind, card.PosterUrl, card.Item.Name, card.Item.Year,
                    probeExactUrl: false, cancellationToken);
                if (!string.Equals(resolved, card.PosterUrl, StringComparison.Ordinal))
                {
                    card.PosterUrl = resolved;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // navigated away
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Home poster enrichment failed");
        }
    }

    private void NotifyRowFlags()
    {
        OnPropertyChanged(nameof(HasContinueWatching));
        OnPropertyChanged(nameof(HasRecentlyWatched));
        OnPropertyChanged(nameof(HasFavoriteChannels));
        OnPropertyChanged(nameof(HasRecentMovies));
        OnPropertyChanged(nameof(HasRecentSeries));
    }

    [RelayCommand]
    private void PlayContinue(ContinueCard? card)
    {
        // Continue-watching resumes are opened via the detail page (movie) — series keys are
        // composite; both route through the catalog lookup on the detail page.
        if (card is null)
        {
            return;
        }

        _ = OpenVodByKeyAsync(card.Entry.ItemKind, card.Entry.ItemKey);
    }

    [RelayCommand]
    private void PlayRecent(RecentWatchCard? card)
    {
        if (card is null)
        {
            return;
        }

        if (card.Channel is { } channel)
        {
            _playback.PlayChannel(channel);
        }
        else if (!card.IsLive)
        {
            _ = OpenVodByKeyAsync(card.Entry.ItemKind, card.Entry.ItemKey);
        }
    }

    [RelayCommand]
    private void PlayChannel(FavoriteChannelCard? card)
    {
        if (card is not null)
        {
            _playback.PlayChannel(card.Channel);
        }
    }

    [RelayCommand]
    private void OpenVod(VodCard? card)
    {
        if (card is not null)
        {
            _navigation.NavigateTo<VodDetailViewModel>(card.Item);
        }
    }

    private async Task OpenVodByKeyAsync(ContentKind kind, string itemKey)
    {
        if (_session.CurrentProfile is not { } profile)
        {
            return;
        }

        // Series episode keys are "seriesId:episodeId"; fall back to the series id.
        var lookupKind = kind == ContentKind.Live ? ContentKind.Movie : kind;
        var providerId = itemKey.Contains(':', StringComparison.Ordinal)
            ? itemKey[..itemKey.IndexOf(':', StringComparison.Ordinal)]
            : itemKey;

        var item = await _catalog.GetVodItemByProviderIdAsync(
            profile.Id, lookupKind, providerId, CancellationToken.None);
        if (item is not null)
        {
            _navigation.NavigateTo<VodDetailViewModel>(item);
        }
    }

    public void Receive(CatalogRefreshedMessage message)
    {
        if (_session.CurrentProfile?.Id == message.ProfileId)
        {
            _ = OnNavigatedToAsync(null, CancellationToken.None);
        }
    }
}
