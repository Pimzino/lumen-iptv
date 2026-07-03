using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;

namespace Lumen.App.ViewModels;

/// <summary>A favorited item shown on the Favorites page.</summary>
public sealed partial class FavoriteCard : ObservableObject
{
    public required ContentKind Kind { get; init; }

    public required string ItemKey { get; init; }

    public required string Name { get; init; }

    public string? ImageUrl { get; init; }

    public Channel? Channel { get; init; }

    public VodItem? VodItem { get; init; }

    public string Monogram => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public bool IsLive => Kind == ContentKind.Live;
}

/// <summary>Favorites page: favorited channels, movies, and series, grouped by kind.</summary>
public sealed partial class FavoritesViewModel : ObservableObject, INavigationAware
{
    private readonly IFavoritesRepository _favorites;
    private readonly ICatalogRepository _catalog;
    private readonly ISessionService _session;
    private readonly PlaybackServiceNavigator _playback;
    private readonly INavigationService _navigation;

    public FavoritesViewModel(
        IFavoritesRepository favorites,
        ICatalogRepository catalog,
        ISessionService session,
        PlaybackServiceNavigator playback,
        INavigationService navigation)
    {
        _favorites = favorites;
        _catalog = catalog;
        _session = session;
        _playback = playback;
        _navigation = navigation;
    }

    public ObservableCollection<FavoriteCard> Channels { get; } = [];

    public ObservableCollection<FavoriteCard> Movies { get; } = [];

    public ObservableCollection<FavoriteCard> Series { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isEmpty;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        Channels.Clear();
        Movies.Clear();
        Series.Clear();
        IsLoading = true;

        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoading = false;
            IsEmpty = true;
            return;
        }

        var favorites = await _favorites.GetAllAsync(profile.Id, cancellationToken);

        // Resolve channel favorites.
        var channelIds = favorites.Where(f => f.ItemKind == ContentKind.Live)
            .Select(f => long.TryParse(f.ItemKey, out var id) ? id : -1)
            .Where(id => id >= 0)
            .ToHashSet();
        if (channelIds.Count > 0)
        {
            var all = await _catalog.GetChannelsAsync(profile.Id, null, cancellationToken);
            foreach (var channel in all.Where(c => channelIds.Contains(c.Id)))
            {
                Channels.Add(new FavoriteCard
                {
                    Kind = ContentKind.Live,
                    ItemKey = channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = channel.Name,
                    ImageUrl = channel.LogoUrl,
                    Channel = channel,
                });
            }
        }

        // Resolve VOD favorites.
        await AddVodFavoritesAsync(profile.Id, ContentKind.Movie, favorites, Movies, cancellationToken);
        await AddVodFavoritesAsync(profile.Id, ContentKind.Series, favorites, Series, cancellationToken);

        IsLoading = false;
        IsEmpty = Channels.Count == 0 && Movies.Count == 0 && Series.Count == 0;
    }

    private async Task AddVodFavoritesAsync(
        long profileId, ContentKind kind, IReadOnlyList<FavoriteItem> favorites,
        ObservableCollection<FavoriteCard> target, CancellationToken cancellationToken)
    {
        var keys = favorites.Where(f => f.ItemKind == kind).Select(f => f.ItemKey).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return;
        }

        var items = await _catalog.GetVodItemsAsync(
            profileId, kind, null, VodSortOrder.Name, 5000, 0, cancellationToken);
        foreach (var item in items.Where(i => keys.Contains(i.ProviderItemId)))
        {
            target.Add(new FavoriteCard
            {
                Kind = kind,
                ItemKey = item.ProviderItemId,
                Name = item.Name,
                ImageUrl = item.PosterUrl,
                VodItem = item,
            });
        }
    }

    public void OnNavigatedFrom()
    {
    }

    [RelayCommand]
    private void Activate(FavoriteCard? card)
    {
        if (card is null)
        {
            return;
        }

        if (card.Channel is { } channel)
        {
            _playback.PlayChannel(channel);
        }
        else if (card.VodItem is { } vod)
        {
            _navigation.NavigateTo<VodDetailViewModel>(vod);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(FavoriteCard? card)
    {
        if (card is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        await _favorites.RemoveAsync(profile.Id, card.Kind, card.ItemKey, CancellationToken.None);
        (card.Kind switch
        {
            ContentKind.Live => Channels,
            ContentKind.Movie => Movies,
            _ => Series,
        }).Remove(card);

        IsEmpty = Channels.Count == 0 && Movies.Count == 0 && Series.Count == 0;
    }
}
