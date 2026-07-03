using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
}

/// <summary>A favorite-channel tile with its live now/next.</summary>
public sealed partial class FavoriteChannelCard : ObservableObject
{
    public required Channel Channel { get; init; }

    public string Name => Channel.Name;

    public string? LogoUrl => Channel.LogoUrl;

    public string Monogram => Channel.Name.Length > 0 ? Channel.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    private string? _nowTitle;

    [ObservableProperty]
    private string? _nextTitle;

    [ObservableProperty]
    private double _nowProgress;
}

/// <summary>
/// Home page: Continue watching (from watch history), Favorite channels with live now/next,
/// and Recently added movies/series. Each row loads its own data; the page shows a designed
/// empty state until the library is populated.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, INavigationAware,
    IRecipient<CatalogRefreshedMessage>
{
    private const int RowLimit = 20;

    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly IWatchHistoryRepository _watchHistory;
    private readonly IFavoritesRepository _favorites;
    private readonly IEpgRepository _epg;
    private readonly IClock _clock;
    private readonly PlaybackServiceNavigator _playback;
    private readonly INavigationService _navigation;

    public HomeViewModel(
        ISessionService session,
        ICatalogRepository catalog,
        IWatchHistoryRepository watchHistory,
        IFavoritesRepository favorites,
        IEpgRepository epg,
        IClock clock,
        PlaybackServiceNavigator playback,
        INavigationService navigation,
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
        messenger.RegisterAll(this);
    }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    public ObservableCollection<ContinueCard> ContinueWatching { get; } = [];

    public ObservableCollection<FavoriteChannelCard> FavoriteChannels { get; } = [];

    public ObservableCollection<VodCard> RecentMovies { get; } = [];

    public ObservableCollection<VodCard> RecentSeries { get; } = [];

    public bool HasContinueWatching => ContinueWatching.Count > 0;

    public bool HasFavoriteChannels => FavoriteChannels.Count > 0;

    public bool HasRecentMovies => RecentMovies.Count > 0;

    public bool HasRecentSeries => RecentSeries.Count > 0;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        IsLoading = true;
        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoading = false;
            IsEmpty = true;
            return;
        }

        ProfileName = profile.Name;
        ContinueWatching.Clear();
        FavoriteChannels.Clear();
        RecentMovies.Clear();
        RecentSeries.Clear();

        try
        {
            await LoadContinueWatchingAsync(profile.Id, cancellationToken);
            await LoadFavoriteChannelsAsync(profile.Id, cancellationToken);
            await LoadRecentAsync(profile.Id, ContentKind.Movie, RecentMovies, cancellationToken);
            await LoadRecentAsync(profile.Id, ContentKind.Series, RecentSeries, cancellationToken);
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
        IsEmpty = !HasContinueWatching && !HasFavoriteChannels && !HasRecentMovies && !HasRecentSeries;
        IsLoading = false;
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadContinueWatchingAsync(long profileId, CancellationToken cancellationToken)
    {
        var recent = await _watchHistory.GetRecentAsync(profileId, RowLimit, cancellationToken);
        foreach (var entry in recent.Where(e => e.PositionSeconds > 30 && e.Progress is > 0.01 and < 0.95))
        {
            ContinueWatching.Add(new ContinueCard { Entry = entry });
        }
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
        var cards = new List<FavoriteChannelCard>();
        var xmltvIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            var card = new FavoriteChannelCard { Channel = channel };
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
    }

    private void NotifyRowFlags()
    {
        OnPropertyChanged(nameof(HasContinueWatching));
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

        var items = await _catalog.GetVodItemsAsync(
            profile.Id, lookupKind, null, VodSortOrder.Name, 5000, 0, CancellationToken.None);
        var item = items.FirstOrDefault(i => i.ProviderItemId == providerId);
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
