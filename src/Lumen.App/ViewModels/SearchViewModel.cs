using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A group of search hits (Channels / Movies / Series / On now &amp; next).</summary>
public sealed class SearchGroup
{
    public required string Header { get; init; }

    public required IReadOnlyList<SearchHit> Hits { get; init; }
}

/// <summary>
/// Global search: 300ms-debounced queries across channels, VOD, and EPG programme titles,
/// presented as grouped results in a title-bar dropdown. Owned by the shell for the app's
/// lifetime and opened from anywhere via Ctrl+K.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int PerGroupLimit = 20;

    private readonly ISearchRepository _search;
    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly IClock _clock;
    private readonly PlaybackServiceNavigator _playback;
    private readonly INavigationService _navigation;
    private readonly DispatcherTimer _debounce;

    private CancellationTokenSource? _searchCts;

    public SearchViewModel(
        ISearchRepository search,
        ISessionService session,
        ICatalogRepository catalog,
        IClock clock,
        PlaybackServiceNavigator playback,
        INavigationService navigation)
    {
        _search = search;
        _session = session;
        _catalog = catalog;
        _clock = clock;
        _playback = playback;
        _navigation = navigation;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
    }

    public ObservableCollection<SearchGroup> Groups { get; } = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasQuery;

    [ObservableProperty]
    private bool _hasResults;

    /// <summary>True once a search has completed for the current query (gates the "no results"
    /// message so it never flashes during the debounce/in-flight window).</summary>
    [ObservableProperty]
    private bool _hasSearched;

    /// <summary>Whether the results dropdown is showing under the title-bar search box.</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private double _lastSearchMs;

    /// <summary>Set by the search box so Ctrl+K can move keyboard focus into the field.</summary>
    public Action? FocusRequested { get; set; }

    /// <summary>Focuses the search box and reopens the dropdown if a query is already present.</summary>
    public void RequestFocus()
    {
        if (HasQuery)
        {
            IsOpen = true;
        }

        FocusRequested?.Invoke();
    }

    partial void OnQueryChanged(string value)
    {
        HasQuery = value.Trim().Length > 0;
        HasSearched = false;
        _debounce.Stop();
        if (value.Trim().Length < 2)
        {
            Groups.Clear();
            HasResults = false;
            IsOpen = false;
            return;
        }

        IsOpen = true;
        _debounce.Start();
    }

    private async Task RunSearchAsync()
    {
        var profile = _session.CurrentProfile;
        var text = Query.Trim();
        if (profile is null || text.Length < 2)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearching = true;
        var started = _clock.UtcNow;
        try
        {
            var results = await _search.SearchAsync(
                profile.Id, text, _clock.UtcNow.ToUnixTimeSeconds(), PerGroupLimit, token);
            token.ThrowIfCancellationRequested();

            Groups.Clear();
            AddGroup(Resources.Strings.Nav_LiveTv, results.Channels);
            AddGroup(Resources.Strings.Nav_Movies, results.Movies);
            AddGroup(Resources.Strings.Nav_Series, results.Series);
            AddGroup(Resources.Strings.Search_GroupProgrammes, results.Programmes);

            HasResults = results.TotalCount > 0;
            HasSearched = true;
            LastSearchMs = (_clock.UtcNow - started).TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search failed");
            HasSearched = true;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void AddGroup(string header, IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count > 0)
        {
            Groups.Add(new SearchGroup { Header = header, Hits = hits });
        }
    }

    [RelayCommand]
    private async Task ActivateAsync(SearchHit? hit)
    {
        if (hit is null || _session.CurrentProfile is not { } profile)
        {
            return;
        }

        // Selecting a result dismisses the dropdown and resets the field.
        IsOpen = false;
        Query = string.Empty;

        switch (hit.Kind)
        {
            case ContentKind.Live:
                if (long.TryParse(hit.ItemKey, out var channelId))
                {
                    var channel = await _catalog.GetChannelAsync(channelId, CancellationToken.None);
                    if (channel is not null)
                    {
                        _playback.PlayChannel(channel);
                    }
                }

                break;

            case ContentKind.Movie:
            case ContentKind.Series:
                var item = await _catalog.GetVodItemByProviderIdAsync(
                    profile.Id, hit.Kind, hit.ItemKey, CancellationToken.None);
                if (item is not null)
                {
                    _navigation.NavigateTo<VodDetailViewModel>(item);
                }

                break;
        }
    }
}
