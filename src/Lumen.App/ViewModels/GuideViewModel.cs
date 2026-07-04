using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Lumen.App.Controls.Epg;
using Lumen.App.Services;
using Lumen.Core.Abstractions;
using Lumen.Core.Models;
using Serilog;

namespace Lumen.App.ViewModels;

/// <summary>A day the guide can jump to.</summary>
public sealed record GuideDay(string Label, DateTimeOffset Date);

/// <summary>Programme detail shown in the flyout.</summary>
public sealed partial class ProgrammeDetail : ObservableObject
{
    public required string ChannelName { get; init; }

    public required Channel Channel { get; init; }

    public required Programme Programme { get; init; }

    public string Title => Programme.Title;

    public string TimeRange =>
        $"{Programme.Start.ToLocalTime():ddd d MMM · HH:mm} – {Programme.Stop.ToLocalTime():HH:mm}";

    public string? Description => Programme.Description;

    public string? Category => Programme.Category;
}

/// <summary>
/// EPG guide page: day picker, category filter, jump-to-now, and the custom virtualized
/// timeline. Loads a day's worth of programmes for the visible channels at a time.
/// </summary>
public sealed partial class GuideViewModel : ObservableObject, INavigationAware,
    IRecipient<EpgRefreshedMessage>
{
    private static readonly Category AllCategories = new() { Id = 0, Name = "All channels" };

    private readonly ISessionService _session;
    private readonly ICatalogRepository _catalog;
    private readonly IEpgRepository _epg;
    private readonly PlaybackServiceNavigator _playbackNavigator;
    private readonly IClock _clock;

    private Dictionary<long, string> _mappings = [];
    private List<Category> _allCategories = [];
    private DateTimeOffset _dayStart;

    public GuideViewModel(
        ISessionService session,
        ICatalogRepository catalog,
        IEpgRepository epg,
        PlaybackServiceNavigator playbackNavigator,
        IMessenger messenger,
        IClock clock)
    {
        _session = session;
        _catalog = catalog;
        _epg = epg;
        _playbackNavigator = playbackNavigator;
        _clock = clock;
        messenger.RegisterAll(this);
    }

    /// <summary>The dropdown's (possibly filtered) category choices.</summary>
    [ObservableProperty]
    private IReadOnlyList<Category> _categories = [];

    [ObservableProperty]
    private string _categoryFilter = string.Empty;

    public ObservableCollection<GuideDay> Days { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<GuideRow> _rows = [];

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private GuideDay? _selectedDay;

    [ObservableProperty]
    private DateTimeOffset _timelineStart;

    [ObservableProperty]
    private DateTimeOffset _timelineEnd;

    [ObservableProperty]
    private DateTimeOffset _now;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasData = true;

    [ObservableProperty]
    private ProgrammeDetail? _selectedProgramme;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    /// <summary>Set by the view so the VM can drive jump-to-now scrolling.</summary>
    public Action<DateTimeOffset>? ScrollToTimeRequested { get; set; }

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null)
        {
            IsLoading = false;
            HasData = false;
            return;
        }

        Now = _clock.UtcNow;

        // Build the 7-day picker (today .. +6).
        Days.Clear();
        var today = _clock.UtcNow.ToLocalTime().Date;
        for (var i = 0; i < 7; i++)
        {
            var date = today.AddDays(i);
            var label = i == 0 ? "Today" : i == 1 ? "Tomorrow" : date.ToString("ddd d MMM");
            Days.Add(new GuideDay(label, new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))));
        }

        _allCategories = [AllCategories, .. await _catalog.GetCategoriesAsync(profile.Id, ContentKind.Live, cancellationToken)];
        CategoryFilter = string.Empty;
        Categories = _allCategories;

        var mappings = await _epg.GetMappingsAsync(profile.Id, cancellationToken);
        _mappings = mappings.ToDictionary(m => m.ChannelId, m => m.XmltvId);

        SelectedCategory = Categories[0];
        SelectedDay = Days[0];
        await LoadAsync(cancellationToken);
    }

    public void OnNavigatedFrom()
    {
    }

    partial void OnSelectedCategoryChanged(Category? value) => _ = LoadAsync(CancellationToken.None);

    partial void OnCategoryFilterChanged(string value)
    {
        var filter = value.Trim();
        if (filter.Length == 0)
        {
            Categories = _allCategories;
            return;
        }

        var matches = new List<Category>();
        foreach (var category in _allCategories)
        {
            // "All channels" and the current selection are pinned: dropping the selected
            // category from ItemsSource would clear SelectedItem through the two-way binding
            // and reload the guide mid-keystroke.
            if (ReferenceEquals(category, AllCategories)
                || ReferenceEquals(category, SelectedCategory)
                || category.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(category);
            }
        }

        Categories = matches;
    }

    partial void OnSelectedDayChanged(GuideDay? value)
    {
        if (value is not null)
        {
            _dayStart = value.Date;
            _ = LoadAsync(CancellationToken.None);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var profile = _session.CurrentProfile;
        if (profile is null || SelectedDay is null || SelectedCategory is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            _dayStart = SelectedDay.Date;
            TimelineStart = _dayStart;
            TimelineEnd = _dayStart.AddDays(1);
            Now = _clock.UtcNow;

            var channels = await _catalog.GetChannelsAsync(
                profile.Id, SelectedCategory.Id == 0 ? null : SelectedCategory.Id, cancellationToken);

            // Resolve each channel's xmltv id, then batch-load the day's programmes.
            var xmltvIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var channelXmltv = new List<(Channel Channel, string? XmltvId)>(channels.Count);
            foreach (var channel in channels)
            {
                var xmltvId = _mappings.GetValueOrDefault(channel.Id) ?? channel.EpgChannelId;
                channelXmltv.Add((channel, xmltvId));
                if (!string.IsNullOrEmpty(xmltvId))
                {
                    xmltvIds.Add(xmltvId);
                }
            }

            var programmes = xmltvIds.Count == 0
                ? []
                : await _epg.GetProgrammesAsync(
                    profile.Id, xmltvIds.ToList(),
                    TimelineStart.ToUnixTimeSeconds(), TimelineEnd.ToUnixTimeSeconds(), cancellationToken);

            var byChannel = programmes
                .GroupBy(p => p.ChannelXmltvId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<Programme>)g.OrderBy(p => p.StartUtc).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var rows = channelXmltv
                .Select(c => new GuideRow(
                    c.Channel,
                    c.XmltvId,
                    c.XmltvId is not null && byChannel.TryGetValue(c.XmltvId, out var list) ? list : []))
                .ToList();

            Rows = rows;
            HasData = rows.Count > 0;
            IsLoading = false;

            // Land near "now" when viewing today; otherwise the start of the day.
            var focus = SelectedDay.Date.Date == _clock.UtcNow.ToLocalTime().Date ? Now : TimelineStart.AddHours(18);
            ScrollToTimeRequested?.Invoke(focus);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading guide failed");
            IsLoading = false;
        }
    }

    public void ShowProgramme(GuideRow row, Programme programme)
    {
        SelectedProgramme = new ProgrammeDetail
        {
            ChannelName = row.Channel.Name,
            Channel = row.Channel,
            Programme = programme,
        };
        IsFlyoutOpen = true;
    }

    [RelayCommand]
    private void JumpToNow()
    {
        Now = _clock.UtcNow;
        if (SelectedDay?.Date.Date != _clock.UtcNow.ToLocalTime().Date)
        {
            SelectedDay = Days[0];
        }
        else
        {
            ScrollToTimeRequested?.Invoke(Now);
        }
    }

    [RelayCommand]
    private void CloseFlyout() => IsFlyoutOpen = false;

    [RelayCommand]
    private void WatchChannel()
    {
        if (SelectedProgramme is { } detail)
        {
            IsFlyoutOpen = false;
            _playbackNavigator.PlayChannel(detail.Channel);
        }
    }

    public void Receive(EpgRefreshedMessage message)
    {
        if (_session.CurrentProfile?.Id == message.ProfileId)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }
}
