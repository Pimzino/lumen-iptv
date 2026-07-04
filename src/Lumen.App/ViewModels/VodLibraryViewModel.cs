using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A poster tile in a VOD grid.</summary>
public sealed partial class VodCard : ObservableObject
{
    public VodCard(VodItem item)
    {
        Item = item;
        _posterUrl = item.PosterUrl;
    }

    public VodItem Item { get; }

    public string Name => Item.Name;

    /// <summary>Provider poster, or an external-artwork fill-in resolved after load.</summary>
    [ObservableProperty]
    private string? _posterUrl;

    public string Monogram => Item.Name.Length > 0 ? Item.Name[..1].ToUpperInvariant() : "?";

    public string? MetaLine => Item.Year is { } year
        ? Item.Rating is { } rating ? $"{year} · ★ {rating:0.0}" : year.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : Item.Rating is { } r ? $"★ {r:0.0}" : null;

    [ObservableProperty]
    private bool _isFavorite;
}

/// <summary>
/// Shared logic for the Movies and Series grids: category sidebar, sort, incremental paging,
/// and navigation to the detail page. Subclasses fix the content kind.
/// </summary>
public abstract partial class VodLibraryViewModel : ObservableObject, INavigationAware
{
    private const int PageSize = 120;

    private static readonly Category AllCategories = new() { Id = 0, Name = "All" };

    private readonly ICatalogRepository _catalog;
    private readonly IFavoritesRepository _favorites;
    private readonly ISessionService _session;
    private readonly INavigationService _navigation;
    private readonly ArtworkService _artwork;
    private readonly DispatcherTimer _searchDebounce;

    private CancellationTokenSource? _loadCts;
    private int _loadedCount;
    private bool _hasMore;
    private bool _suppressReload;
    private HashSet<string> _favoriteKeys = [];
    private List<Category> _allCategories = [];
    private Category? _lastRealCategory;

    protected VodLibraryViewModel(
        ICatalogRepository catalog,
        IFavoritesRepository favorites,
        ISessionService session,
        INavigationService navigation,
        ArtworkService artwork)
    {
        _catalog = catalog;
        _favorites = favorites;
        _session = session;
        _navigation = navigation;
        _artwork = artwork;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ReloadAsync();
        };
    }

    protected abstract ContentKind Kind { get; }

    public abstract string Title { get; }

    public ObservableCollection<Category> Categories { get; } = [];

    public ObservableCollection<VodCard> Items { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } = ["Recently added", "Name", "Rating"];

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _categoryFilterText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasItems = true;

    [ObservableProperty]
    private string _emptyMessage = Resources.Strings.Vod_NoItems;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoading = false;
            HasItems = false;
            return;
        }

        _favoriteKeys = (await _favorites.GetAllAsync(profile.Id, cancellationToken))
            .Where(f => f.ItemKind == Kind)
            .Select(f => f.ItemKey)
            .ToHashSet(StringComparer.Ordinal);

        _allCategories = [AllCategories, .. await _catalog.GetCategoriesAsync(profile.Id, Kind, cancellationToken)];
        ApplyCategoryFilter();

        // Suppress the selection hook: it would kick off a second, concurrent reload racing
        // the awaited one below.
        _suppressReload = true;
        SelectedCategory = Categories[0];
        _suppressReload = false;
        await ReloadAsync();
    }

    public void OnNavigatedFrom()
    {
        _searchDebounce.Stop();
        _loadCts?.Cancel();
    }

    private VodSortOrder Sort => SortIndex switch
    {
        1 => VodSortOrder.Name,
        2 => VodSortOrder.Rating,
        _ => VodSortOrder.Added,
    };

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (value is not null)
        {
            _lastRealCategory = value;
        }

        if (!_suppressReload)
        {
            // The reload below already reads the current SearchText; a pending debounce
            // tick would only repeat it.
            _searchDebounce.Stop();
            _ = ReloadAsync();
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        _searchDebounce.Stop();
        _ = ReloadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            _ = ReloadAsync();
        }
        else
        {
            _searchDebounce.Start();
        }
    }

    partial void OnCategoryFilterTextChanged(string value) => ApplyCategoryFilter();

    private void ApplyCategoryFilter()
    {
        var filter = CategoryFilterText.Trim();

        // Removing the selected row makes the ListBox null SelectedCategory synchronously
        // inside Clear(); a null-triggered reload would cancel _loadCts and strand IsLoading.
        // Suppress the hook for the whole refill, then restore the selection if it survived.
        _suppressReload = true;
        try
        {
            Categories.Clear();
            foreach (var category in _allCategories)
            {
                // "All" is pinned: it's the default selection and the escape hatch.
                if (ReferenceEquals(category, AllCategories)
                    || filter.Length == 0
                    || category.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    Categories.Add(category);
                }
            }

            if (SelectedCategory is null && _lastRealCategory is { } last && Categories.Contains(last))
            {
                SelectedCategory = last;
            }
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        var profile = _session.CurrentProfile;
        if (profile is null || SelectedCategory is null)
        {
            return;
        }

        IsLoading = true;
        Items.Clear();
        _loadedCount = 0;

        try
        {
            await LoadPageAsync(profile.Id, token);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading VOD library failed");
        }
        finally
        {
            // A superseded reload must not clear the loading state the newer reload owns.
            if (!token.IsCancellationRequested)
            {
                HasItems = Items.Count > 0;
                EmptyMessage = string.IsNullOrWhiteSpace(SearchText)
                    ? Resources.Strings.Vod_NoItems
                    : Resources.Strings.Filter_NoMatches;
                IsLoading = false;
            }
        }
    }

    private async Task LoadPageAsync(long profileId, CancellationToken cancellationToken)
    {
        var page = await _catalog.GetVodItemsAsync(
            profileId, Kind, SelectedCategory!.Id == 0 ? null : SelectedCategory.Id,
            SearchText, Sort, PageSize, _loadedCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var added = new List<VodCard>(page.Count);
        foreach (var item in page)
        {
            var card = new VodCard(item) { IsFavorite = _favoriteKeys.Contains(item.ProviderItemId) };
            Items.Add(card);
            added.Add(card);
        }

        _loadedCount += page.Count;
        _hasMore = page.Count == PageSize;

        // Fill artwork gaps in the background; cancelled by the next reload/navigation.
        _ = EnrichPostersAsync(added, cancellationToken);
    }

    private async Task EnrichPostersAsync(IReadOnlyList<VodCard> cards, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var card in cards)
            {
                if (!string.IsNullOrWhiteSpace(card.PosterUrl))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var art = await _artwork.GetArtworkAsync(Kind, card.Item.Name, card.Item.Year, cancellationToken);
                if (art?.PosterUrl is { } poster)
                {
                    card.PosterUrl = poster;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // superseded page or navigation away
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Poster enrichment pass failed");
        }
    }

    /// <summary>Called by the view as the grid nears its end (infinite scroll).</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!_hasMore || IsLoading || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        try
        {
            await LoadPageAsync(profile.Id, _loadCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void OpenDetail(VodCard? card)
    {
        if (card is not null)
        {
            _navigation.NavigateTo<VodDetailViewModel>(card.Item);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(VodCard? card)
    {
        if (card is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        if (card.IsFavorite)
        {
            await _favorites.RemoveAsync(profile.Id, Kind, card.Item.ProviderItemId, CancellationToken.None);
            card.IsFavorite = false;
            _favoriteKeys.Remove(card.Item.ProviderItemId);
        }
        else
        {
            await _favorites.AddAsync(
                profile.Id, Kind, card.Item.ProviderItemId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CancellationToken.None);
            card.IsFavorite = true;
            _favoriteKeys.Add(card.Item.ProviderItemId);
        }
    }
}

/// <summary>Movies grid.</summary>
public sealed class MoviesViewModel : VodLibraryViewModel
{
    public MoviesViewModel(
        ICatalogRepository catalog, IFavoritesRepository favorites,
        ISessionService session, INavigationService navigation, ArtworkService artwork)
        : base(catalog, favorites, session, navigation, artwork)
    {
    }

    protected override ContentKind Kind => ContentKind.Movie;

    public override string Title => Resources.Strings.Nav_Movies;
}

/// <summary>Series grid.</summary>
public sealed class SeriesViewModel : VodLibraryViewModel
{
    public SeriesViewModel(
        ICatalogRepository catalog, IFavoritesRepository favorites,
        ISessionService session, INavigationService navigation, ArtworkService artwork)
        : base(catalog, favorites, session, navigation, artwork)
    {
    }

    protected override ContentKind Kind => ContentKind.Series;

    public override string Title => Resources.Strings.Nav_Series;
}
