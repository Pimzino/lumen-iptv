using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Services;
using Lumen.App.Services.Playback;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A channel row in the Live TV list, carrying its live EPG state.</summary>
public sealed partial class ChannelListItem : ObservableObject
{
    public ChannelListItem(Channel channel)
    {
        Channel = channel;
    }

    public Channel Channel { get; }

    public string Name => Channel.Name;

    public string? LogoUrl => Channel.LogoUrl;

    public string Monogram => Channel.Name.Length > 0 ? Channel.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    private string? _nowTitle;

    [ObservableProperty]
    private string? _nowTimeRange;

    [ObservableProperty]
    private double _nowProgress;

    [ObservableProperty]
    private string? _nextTitle;

    internal Programme? NowProgramme { get; set; }

    internal string? XmltvId { get; set; }

    internal void UpdateProgress(DateTimeOffset now)
    {
        if (NowProgramme is { } programme)
        {
            NowProgress = programme.ProgressAt(now) * 100;
        }
    }
}

/// <summary>
/// Live TV: categories → virtualized channel list (with now/next + progress) → muted
/// preview pane with a Watch call-to-action that expands into the full player.
/// </summary>
public sealed partial class LiveTvViewModel : ObservableObject, INavigationAware
{
    /// <summary>Synthetic "All channels" category (id 0).</summary>
    private static readonly Category AllChannels = new() { Id = 0, Name = "All channels" };

    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly IEpgRepository _epg;
    private readonly PlaybackService _playbackService;
    private readonly IClock _clock;
    private readonly DispatcherTimer _progressTimer;

    private CancellationTokenSource? _channelsCts;
    private CancellationTokenSource? _previewCts;
    private Dictionary<long, string> _mappings = [];

    public LiveTvViewModel(
        ISessionService session,
        ICatalogRepository catalog,
        IEpgRepository epg,
        PlaybackService playbackService,
        IClock clock)
    {
        _session = session;
        _catalog = catalog;
        _epg = epg;
        _playbackService = playbackService;
        _clock = clock;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _progressTimer.Tick += (_, _) => TickProgress();
    }

    public PlaybackService Playback => _playbackService;

    public ObservableCollection<Category> Categories { get; } = [];

    /// <summary>Replaced wholesale per category — one collection reset instead of thousands of Adds.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChannelListItem> _channels = [];

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private ChannelListItem? _selectedChannel;

    [ObservableProperty]
    private bool _isLoadingChannels = true;

    [ObservableProperty]
    private bool _hasChannels = true;

    [ObservableProperty]
    private string? _channelCountLabel;

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoadingChannels = false;
            HasChannels = false;
            return;
        }

        var mappingRows = await _epg.GetMappingsAsync(profile.Id, cancellationToken);
        _mappings = mappingRows.ToDictionary(m => m.ChannelId, m => m.XmltvId);

        Categories.Clear();
        Categories.Add(AllChannels);
        foreach (var category in await _catalog.GetCategoriesAsync(profile.Id, ContentKind.Live, cancellationToken))
        {
            Categories.Add(category);
        }

        SelectedCategory = Categories[0];
        _progressTimer.Start();
    }

    public void OnNavigatedFrom()
    {
        _progressTimer.Stop();
        _channelsCts?.Cancel();
        _previewCts?.Cancel();

        // Leaving the page kills the muted preview; full/mini playback continues.
        if (!_playbackService.IsFullPlayerActive && !_playbackService.IsMiniPlayerActive)
        {
            _playbackService.Stop();
        }
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (value is not null)
        {
            _ = LoadChannelsAsync(value);
        }
    }

    partial void OnSelectedChannelChanged(ChannelListItem? value)
    {
        // Don't hijack the shared video with a muted preview while the user is actively watching in
        // the full player or the floating mini player — that would steal the surface and mute it.
        if (value is not null
            && !_playbackService.IsFullPlayerActive
            && !_playbackService.IsMiniPlayerActive)
        {
            _ = StartPreviewAsync(value);
        }
    }

    private async Task LoadChannelsAsync(Category category)
    {
        _channelsCts?.Cancel();
        _channelsCts?.Dispose();
        _channelsCts = new CancellationTokenSource();
        var token = _channelsCts.Token;

        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            return;
        }

        IsLoadingChannels = true;
        try
        {
            var channels = await _catalog.GetChannelsAsync(
                profile.Id, category.Id == 0 ? null : category.Id, token);
            token.ThrowIfCancellationRequested();

            Channels = channels.Select(channel => new ChannelListItem(channel)).ToList();

            HasChannels = Channels.Count > 0;
            ChannelCountLabel = $"{Channels.Count:N0}";
            IsLoadingChannels = false;

            await FillNowNextAsync(profile.Id, token);
        }
        catch (OperationCanceledException)
        {
            // superseded by another category selection
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading channels failed");
            IsLoadingChannels = false;
        }
    }

    private async Task FillNowNextAsync(long profileId, CancellationToken cancellationToken)
    {
        var byXmltv = new Dictionary<string, List<ChannelListItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Channels)
        {
            var xmltvId = _mappings.GetValueOrDefault(item.Channel.Id) ?? item.Channel.EpgChannelId;
            if (string.IsNullOrEmpty(xmltvId))
            {
                continue;
            }

            item.XmltvId = xmltvId;
            if (!byXmltv.TryGetValue(xmltvId, out var list))
            {
                byXmltv[xmltvId] = list = [];
            }

            list.Add(item);
        }

        if (byXmltv.Count == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        var nowNext = await _epg.GetNowNextAsync(
            profileId, byXmltv.Keys.ToList(), now.ToUnixTimeSeconds(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var (xmltvId, items) in byXmltv)
        {
            if (!nowNext.TryGetValue(xmltvId, out var entry))
            {
                continue;
            }

            foreach (var item in items)
            {
                item.NowProgramme = entry.Now;
                item.NowTitle = entry.Now?.Title;
                item.NowTimeRange = entry.Now is { } airing
                    ? $"{airing.Start.ToLocalTime():HH:mm}–{airing.Stop.ToLocalTime():HH:mm}"
                    : null;
                item.NextTitle = entry.Next?.Title;
                item.UpdateProgress(now);
            }
        }
    }

    private void TickProgress()
    {
        var now = _clock.UtcNow;
        var needsRefresh = false;
        foreach (var item in Channels)
        {
            item.UpdateProgress(now);
            if (item.NowProgramme is { } programme && now.ToUnixTimeSeconds() > programme.StopUtc)
            {
                needsRefresh = true;
            }
        }

        if (needsRefresh && _session.CurrentProfile is { } profile)
        {
            _ = FillNowNextAsync(profile.Id, _channelsCts?.Token ?? CancellationToken.None);
        }
    }

    /// <summary>Debounced muted preview of the selected channel.</summary>
    private async Task StartPreviewAsync(ChannelListItem item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            await Task.Delay(350, token);
            _playbackService.ActivateSurface(VideoSurfaceKind.Preview);
            await _playbackService.PlayChannelAsync(
                item.Channel, Channels.Select(c => c.Channel).ToList(), preview: true, token);
        }
        catch (OperationCanceledException)
        {
            // selection moved on
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Preview failed");
        }
    }

    [RelayCommand]
    private void Watch()
    {
        if (SelectedChannel is null)
        {
            return;
        }

        if (_playbackService.State is PlaybackState.Idle or PlaybackState.Error)
        {
            _ = _playbackService.PlayChannelAsync(
                SelectedChannel.Channel, Channels.Select(c => c.Channel).ToList(), preview: false,
                CancellationToken.None);
        }

        _playbackService.EnterFullPlayer();
    }
}
